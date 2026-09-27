using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NewsWatch.Models;

namespace NewsWatch.Services;

public sealed class ParsedFeed
{
    public required string Format { get; init; }  // RSS / RDF / ATOM / JSON
    public string Title { get; init; } = "";
    public List<FeedItem> Items { get; init; } = new();
}

public sealed class FeedParseException(string code) : Exception(code)
{
    /// <summary>Short code for the feed list: NOT A FEED, BAD XML, BAD JSON.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Namespace-agnostic parser for RSS 0.9x/2.0, RSS 1.0 (RDF), Atom and JSON
/// Feed. Matches elements by local name so feeds that mis-declare or omit
/// namespaces (common) still parse. Everything handed out is plain text.
/// </summary>
public static partial class FeedParser
{
    private const int MaxSummaryChars = 400;

    // Feed content is untrusted: bound everything that is kept (seen.json, history)
    // so a hostile feed can't fill memory or disk with a few giant fields.
    private const int MaxItems = 1000;
    private const int MaxTitleChars = 300;
    private const int MaxFeedTitleChars = 200;
    private const int MaxUrlChars = 2048;

    // windows-1252, iso-8859-x etc. aren't available in .NET without this; older feeds still use them.
    static FeedParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ParsedFeed Parse(byte[] body, Uri baseUri)
    {
        var start = SkipBomAndWhitespace(body);
        if (start < body.Length && body[start] == (byte)'{')
            return ParseJsonFeed(body.AsMemory(start), baseUri); // past any BOM: JsonDocument rejects it

        var doc = LoadXml(body);
        var root = doc.Root ?? throw new FeedParseException("NOT A FEED");
        return root.Name.LocalName.ToLowerInvariant() switch
        {
            "rss" => ParseRss(root, baseUri, "RSS"),
            "rdf" => ParseRss(root, baseUri, "RDF"),
            "feed" => ParseAtom(root, baseUri),
            _ => throw new FeedParseException("NOT A FEED")
        };
    }

    /// <summary>
    /// True when the body looks like an HTML page rather than a feed (for
    /// autodiscovery): an &lt;html&gt; or HTML doctype before any feed root.
    /// Looks past leading comments, which some sites (Slashdot) put first.
    /// </summary>
    public static bool LooksLikeHtml(byte[] body)
    {
        var head = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 4096));
        var html = FirstIndex(head, "<!doctype html", "<html");
        if (html < 0) return false;
        var feed = FirstIndex(head, "<rss", "<feed", "<rdf:rdf");
        return feed < 0 || html < feed;
    }

    private static int FirstIndex(string text, params string[] needles)
    {
        var best = -1;
        foreach (var n in needles)
        {
            var i = text.IndexOf(n, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        return best;
    }

    // ===== XML =====

    private static XDocument LoadXml(byte[] body)
    {
        try
        {
            return ReadXml(body);
        }
        catch (XmlException)
        {
            // Many feeds use HTML entities (&nbsp; &mdash; ...) that XML doesn't
            // define. Rewrite those to numeric references and try once more.
            var text = DecodeText(body);
            var repaired = NamedEntity().Replace(text, m =>
            {
                var name = m.Groups[1].Value;
                if (name is "amp" or "lt" or "gt" or "quot" or "apos") return m.Value;
                var decoded = WebUtility.HtmlDecode(m.Value);
                return decoded == m.Value
                    ? "&amp;" + name + ";" // unknown entity: keep it as literal text
                    : string.Concat(decoded.EnumerateRunes().Select(r => $"&#{r.Value};"));
            });
            try
            {
                return ReadXml(Encoding.UTF8.GetBytes(StripXmlDecl(repaired)));
            }
            catch (XmlException)
            {
                throw new FeedParseException("BAD XML");
            }
        }
    }

    private static XDocument ReadXml(byte[] body)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, // no DTDs, no external entities (XXE)
            XmlResolver = null,
            CheckCharacters = false,               // tolerate stray control characters
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using var reader = XmlReader.Create(new MemoryStream(body), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Bytes to text, honouring a BOM or the XML declaration's encoding; UTF-8 otherwise.</summary>
    private static string DecodeText(byte[] body)
    {
        var encoding = Encoding.UTF8;
        var head = Encoding.ASCII.GetString(body, 0, Math.Min(body.Length, 200));
        var m = XmlEncodingDecl().Match(head);
        if (m.Success)
        {
            // Unknown names throw ArgumentException; UTF-7 throws NotSupportedException.
            try { encoding = Encoding.GetEncoding(m.Groups[1].Value); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        using var reader = new StreamReader(new MemoryStream(body), encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The text is re-encoded as UTF-8, so its declaration (and the encoding it names) must go entirely.</summary>
    private static string StripXmlDecl(string text) => XmlDecl().Replace(text.TrimStart('﻿'), "", 1);

    private static ParsedFeed ParseRss(XElement root, Uri baseUri, string format)
    {
        var channel = Child(root, "channel");
        // RSS 2.0: items live in <channel>. RSS 1.0: they're siblings of it.
        var items = (channel?.Elements().Where(e => Is(e, "item")) ?? Enumerable.Empty<XElement>())
            .Concat(root.Elements().Where(e => Is(e, "item")));

        var feedLink = ResolveUrl(Text(Child(channel, "link")), baseUri) is string l ? new Uri(l) : baseUri;
        var list = new List<FeedItem>();
        foreach (var item in items)
        {
            if (list.Count == MaxItems) break;
            var title = CleanText(Text(Child(item, "title")));
            var link = ResolveUrl(Text(Child(item, "link")), feedLink)
                       ?? ResolveUrl(Child(item, "link")?.Attribute("href")?.Value, feedLink) // atom:link inside RSS
                       ?? PermalinkGuid(item, feedLink);
            var description = Text(Child(item, "description")) ?? Text(Child(item, "encoded")) ?? Text(Child(item, "summary"));
            var summary = CleanText(description);
            if (title.Length == 0 && summary.Length == 0) continue;

            list.Add(new FeedItem
            {
                Id = ItemId(Text(Child(item, "guid")), item.Attributes().FirstOrDefault(a => a.Name.LocalName == "about")?.Value, link, title + "|" + summary),
                Title = TitleFor(title, summary),
                Link = link,
                Summary = SummaryFor(summary, title),
                Published = ParseDate(Text(Child(item, "pubDate")) ?? Text(Child(item, "date")) ?? Text(Child(item, "published")) ?? Text(Child(item, "updated"))),
                ImageUrl = FindImage(item, feedLink) ?? FirstImgSrc(description, feedLink)
            });
        }
        return new ParsedFeed { Format = format, Title = FeedTitle(Text(Child(channel, "title"))), Items = list };
    }

    private static string? PermalinkGuid(XElement item, Uri baseUri)
    {
        var guid = Child(item, "guid");
        if (guid == null || guid.Attribute("isPermaLink")?.Value == "false") return null;
        return ResolveUrl(guid.Value, baseUri);
    }

    private static ParsedFeed ParseAtom(XElement root, Uri baseUri)
    {
        var feedBase = XmlBase(root, baseUri);
        var list = new List<FeedItem>();
        foreach (var entry in root.Elements().Where(e => Is(e, "entry")))
        {
            if (list.Count == MaxItems) break;
            var entryBase = XmlBase(entry, feedBase);
            var links = entry.Elements().Where(e => Is(e, "link")).ToList();
            var alternate = links.FirstOrDefault(e => (e.Attribute("rel")?.Value ?? "alternate") == "alternate") ?? links.FirstOrDefault();
            var link = ResolveUrl(alternate?.Attribute("href")?.Value, entryBase);
            var title = CleanText(Text(Child(entry, "title")));
            var raw = Text(Child(entry, "summary")) ?? Text(Child(entry, "content"));
            var summary = CleanText(raw);
            if (title.Length == 0 && summary.Length == 0) continue;

            var enclosure = links.FirstOrDefault(e => e.Attribute("rel")?.Value == "enclosure" &&
                                                      (e.Attribute("type")?.Value ?? "").StartsWith("image/"));
            list.Add(new FeedItem
            {
                Id = ItemId(Text(Child(entry, "id")), link, title + "|" + summary),
                Title = TitleFor(title, summary),
                Link = link,
                Summary = SummaryFor(summary, title),
                Published = ParseDate(Text(Child(entry, "published")) ?? Text(Child(entry, "updated")) ?? Text(Child(entry, "issued"))),
                ImageUrl = FindImage(entry, entryBase)
                           ?? ResolveUrl(enclosure?.Attribute("href")?.Value, entryBase)
                           ?? FirstImgSrc(raw, entryBase)
            });
        }
        return new ParsedFeed { Format = "ATOM", Title = FeedTitle(Text(Child(root, "title"))), Items = list };
    }

    private static Uri XmlBase(XElement element, Uri parent)
    {
        var value = element.Attribute(XNamespace.Xml + "base")?.Value;
        return value != null && Uri.TryCreate(parent, value, out var uri) ? uri : parent;
    }

    /// <summary>media:thumbnail, media:content (image), media:group, enclosure (image), itunes:image.</summary>
    private static string? FindImage(XElement item, Uri baseUri)
    {
        foreach (var e in item.Descendants())
        {
            var name = e.Name.LocalName;
            var type = e.Attribute("type")?.Value ?? "";
            var url = e.Attribute("url")?.Value;
            if (name == "thumbnail" && url != null) return ResolveUrl(url, baseUri);
            if (name == "content" && url != null &&
                (e.Attribute("medium")?.Value == "image" || type.StartsWith("image/") || LooksLikeImageUrl(url)))
                return ResolveUrl(url, baseUri);
            if (name == "enclosure" && url != null && (type.StartsWith("image/") || LooksLikeImageUrl(url)))
                return ResolveUrl(url, baseUri);
            if (name == "image" && e.Attribute("href")?.Value is string href)
                return ResolveUrl(href, baseUri);
        }
        return null;
    }

    private static bool LooksLikeImageUrl(string url)
        => Regex.IsMatch(url, @"\.(jpe?g|png|gif|webp)(\?|$)", RegexOptions.IgnoreCase);

    private static string? FirstImgSrc(string? html, Uri baseUri)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = ImgSrc().Match(html);
        return m.Success ? ResolveUrl(WebUtility.HtmlDecode(m.Groups[1].Value), baseUri) : null;
    }

    // ===== JSON Feed (jsonfeed.org, v1 / v1.1) =====

    private static ParsedFeed ParseJsonFeed(ReadOnlyMemory<byte> body, Uri baseUri)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new FeedParseException("BAD JSON");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new FeedParseException("NOT A FEED");

            var list = new List<FeedItem>();
            foreach (var item in items.EnumerateArray())
            {
                if (list.Count == MaxItems) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var link = ResolveUrl(Str(item, "url") ?? Str(item, "external_url"), baseUri);
                var title = CleanText(Str(item, "title"));
                var raw = Str(item, "summary") ?? Str(item, "content_text") ?? Str(item, "content_html");
                var summary = CleanText(raw);
                if (title.Length == 0 && summary.Length == 0) continue;
                list.Add(new FeedItem
                {
                    Id = ItemId(Str(item, "id"), link, title + "|" + summary),
                    Title = TitleFor(title, summary),
                    Link = link,
                    Summary = SummaryFor(summary, title),
                    Published = ParseDate(Str(item, "date_published") ?? Str(item, "date_modified")),
                    ImageUrl = ResolveUrl(Str(item, "image") ?? Str(item, "banner_image"), baseUri)
                               ?? FirstImgSrc(Str(item, "content_html"), baseUri)
                });
            }
            return new ParsedFeed { Format = "JSON", Title = FeedTitle(Str(root, "title")), Items = list };
        }
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(), // some feeds use numeric ids
            _ => null
        };
    }

    // ===== Helpers =====

    private static bool Is(XElement e, string localName)
        => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

    private static XElement? Child(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(e => Is(e, localName));

    private static string? Text(XElement? e)
    {
        if (e == null) return null;
        // Atom type="xhtml" puts markup in child elements; flatten to its text.
        var value = e.HasElements && e.Attribute("type")?.Value == "xhtml"
            ? string.Concat(e.DescendantNodes().OfType<XText>().Select(t => t.Value))
            : e.Value;
        value = value.Trim();
        return value.Length > 0 ? value : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    /// <summary>First non-empty candidate (the last one is never empty), size-capped as a seen key.</summary>
    private static string ItemId(params string?[] candidates) => FeedItem.CompactKey(FirstNonEmpty(candidates)!);

    private static string TitleFor(string title, string summary)
        => title.Length > 0 ? Truncate(title, MaxTitleChars) : Truncate(summary, 120);

    private static string FeedTitle(string? raw) => Truncate(CleanText(raw), MaxFeedTitleChars);

    /// <summary>Absolute http(s) URL, resolving relative links; null for anything else (javascript:, mailto:, garbage, oversized).</summary>
    public static string? ResolveUrl(string? value, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(baseUri, value.Trim(), out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        var absolute = uri.AbsoluteUri;
        return absolute.Length <= MaxUrlChars ? absolute : null;
    }

    /// <summary>
    /// HTML (or double-encoded text) to one line of plain text: drops script,
    /// style and tags, decodes entities, collapses whitespace.
    /// </summary>
    public static string CleanText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var text = StripScriptAndStyle(html);
        text = BlockTag().Replace(text, " ");
        text = AnyTag().Replace(text, "");
        text = WebUtility.HtmlDecode(WebUtility.HtmlDecode(text)); // some feeds double-encode
        text = Whitespace().Replace(text, " ").Trim();
        return text;
    }

    /// <summary>
    /// Drops &lt;script&gt; and &lt;style&gt; elements in one forward pass. An
    /// unclosed one runs to the end of the text (as in a browser), so a flood
    /// of unclosed tags can't make each one rescan the rest.
    /// </summary>
    private static string StripScriptAndStyle(string html)
    {
        var m = ScriptOrStyleOpen().Match(html);
        if (!m.Success) return html;
        var sb = new StringBuilder(html.Length);
        var pos = 0;
        while (m.Success)
        {
            sb.Append(html, pos, m.Index - pos).Append(' ');
            var close = html.IndexOf("</" + m.Groups[1].Value, m.Index + m.Length, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return sb.ToString();
            var end = html.IndexOf('>', close);
            pos = end < 0 ? html.Length : end + 1;
            m = ScriptOrStyleOpen().Match(html, pos);
        }
        return sb.Append(html, pos, html.Length - pos).ToString();
    }

    /// <summary>The summary, unless it only restates the headline (Google News, many aggregators).</summary>
    private static string SummaryFor(string summary, string title)
    {
        if (summary == title) return "";
        var probe = title.Length >= 20 ? title[..Math.Min(40, title.Length)] : null;
        if (probe != null && summary.StartsWith(probe, StringComparison.OrdinalIgnoreCase)) return "";
        return Truncate(summary, MaxSummaryChars);
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', max - 1);
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd(',', ';', ':', '-', ' ') + "…";
    }

    // RFC 822 zones .NET doesn't know; ISO 8601 and numeric offsets parse natively.
    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UT"] = "+0000", ["UTC"] = "+0000", ["GMT"] = "+0000", ["Z"] = "+0000",
        ["BST"] = "+0100", ["CET"] = "+0100", ["CEST"] = "+0200",
        ["EST"] = "-0500", ["EDT"] = "-0400", ["CST"] = "-0600", ["CDT"] = "-0500",
        ["MST"] = "-0700", ["MDT"] = "-0600", ["PST"] = "-0800", ["PDT"] = "-0700",
        ["AEST"] = "+1000", ["AEDT"] = "+1100", ["IST"] = "+0530", ["JST"] = "+0900"
    };

    private static readonly string[] Rfc822Formats =
    {
        "d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz", "d MMM yy HH:mm zzz",
        "d MMMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss", "d MMM yyyy"
    };

    public static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 100) return null; // no real date is this long

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var iso))
            return iso.UtcDateTime;

        // RFC 822: drop the weekday, map a zone name to an offset, "+0100" -> "+01:00".
        var s = Regex.Replace(value, @"^[A-Za-z]{3,9},?\s*", "");
        s = Regex.Replace(s, @"\s([A-Za-z]{1,4})$", m => Zones.TryGetValue(m.Groups[1].Value, out var off) ? " " + off : m.Value);
        s = Regex.Replace(s, @"([+-]\d{2})(\d{2})$", "$1:$2");
        if (DateTimeOffset.TryParseExact(s, Rfc822Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var rfc))
            return rfc.UtcDateTime;
        return null;
    }

    private static int SkipBomAndWhitespace(byte[] body)
    {
        int i = body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0;
        while (i < body.Length && body[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        return i;
    }

    [GeneratedRegex(@"&([A-Za-z][A-Za-z0-9]{1,31});")]
    private static partial Regex NamedEntity();

    [GeneratedRegex(@"<\?xml[^>]*?encoding\s*=\s*[""']([A-Za-z0-9._-]+)[""']")]
    private static partial Regex XmlEncodingDecl();

    [GeneratedRegex(@"^\s*<\?xml[^>]*\?>")]
    private static partial Regex XmlDecl();

    // The HTML regexes below run over feed-supplied text, so they use NonBacktracking:
    // linear time however the input is crafted (e.g. thousands of unclosed "<" or "<img").

    [GeneratedRegex(@"<img[^>]+src\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex ImgSrc();

    [GeneratedRegex(@"<(script|style)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex ScriptOrStyleOpen();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.NonBacktracking)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
