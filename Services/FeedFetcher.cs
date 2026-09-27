using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace NewsWatch.Services;

public enum FetchOutcome
{
    Ok,
    NotModified,
    Error
}

public sealed class FetchResult
{
    public required FetchOutcome Outcome { get; init; }
    public ParsedFeed? Feed { get; init; }

    /// <summary>Short failure code for the feed list (TIMEOUT, DNS, HTTP 404, NOT A FEED, ...).</summary>
    public string? Error { get; init; }

    /// <summary>A network-level failure (no answer, no route, dropped) worth retrying soon, as opposed to what the server said.</summary>
    public bool Transient { get; init; }

    /// <summary>Set when the URL was a web page and a feed was found through its &lt;link rel="alternate"&gt;.</summary>
    public string? DiscoveredUrl { get; init; }

    public string? ETag { get; init; }
    public string? LastModified { get; init; }
}

/// <summary>
/// Downloads and parses feeds. One shared HttpClient (connection reuse across
/// feeds on the same host); conditional GET so an unchanged feed costs a 304.
/// </summary>
public static partial class FeedFetcher
{
    private const int MaxBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = CreateClient(publicOnly: false);

    // Thumbnails: the URL is chosen by the feed, not the user, so it may only reach public addresses.
    private static readonly HttpClient ImageHttp = CreateClient(publicOnly: true);

    private static HttpClient CreateClient(bool publicOnly)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), // pick up DNS changes
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync
        };
        if (publicOnly)
        {
            // The address check sits in the connect step, so it sees what the host name
            // actually resolved to and covers every redirect hop. A proxy would make the
            // connection on our behalf (and is often on localhost itself), so none is used.
            handler.UseProxy = false;
            handler.ConnectCallback = ConnectPublicOnlyAsync;
        }
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        // Some publishers (Reddit, Cloudflare-fronted sites) reject requests without a browser-ish UA.
        var version = typeof(FeedFetcher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64; compatible; NewsWatch/{version}; RSS reader)");
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "application/rss+xml, application/atom+xml, application/feed+json, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.8");
        return client;
    }

    /// <param name="discover">Follow a web page's feed link when the URL isn't a feed itself (used by the add/edit dialog).</param>
    public static async Task<FetchResult> FetchAsync(string url, string? etag, string? lastModified,
        CancellationToken token, bool discover = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            var result = await FetchCoreAsync(url, etag, lastModified, timeout.Token).ConfigureAwait(false);
            if (discover && result.Html is { } html && result.Error == null)
            {
                var found = DiscoverFeed(html, result.FinalUri);
                if (found == null) return Fail("NO FEED ON PAGE");
                var feedResult = await FetchCoreAsync(found, null, null, timeout.Token).ConfigureAwait(false);
                return feedResult.Error != null
                    ? Fail(feedResult.Error)
                    : feedResult.Html != null ? Fail("NOT A FEED") : feedResult.ToResult(found);
            }
            if (result.Html != null) return Fail("NOT A FEED");
            return result.Error != null ? Fail(result.Error) : result.ToResult(null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Fail("TIMEOUT", transient: true);
        }
        catch (HttpRequestException ex)
        {
            return Fail(Classify(ex), transient: ex.StatusCode == null);
        }
        catch (IOException)
        {
            return Fail("CONN LOST", transient: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail("FAIL");
        }
    }

    private static FetchResult Fail(string code, bool transient = false)
        => new() { Outcome = FetchOutcome.Error, Error = code, Transient = transient };

    private sealed class RawResult
    {
        public string? Error { get; init; }
        public bool NotModified { get; init; }
        public ParsedFeed? Feed { get; init; }
        public byte[]? Html { get; init; }
        public required Uri FinalUri { get; init; }
        public string? ETag { get; init; }
        public string? LastModified { get; init; }

        public FetchResult ToResult(string? discovered) => new()
        {
            Outcome = NotModified ? FetchOutcome.NotModified : FetchOutcome.Ok,
            Feed = Feed,
            DiscoveredUrl = discovered,
            ETag = ETag,
            LastModified = LastModified
        };
    }

    private static async Task<RawResult> FetchCoreAsync(string url, string? etag, string? lastModified, CancellationToken token)
    {
        var uri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (lastModified != null) request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        var finalUri = response.RequestMessage?.RequestUri ?? uri;

        if (response.StatusCode == HttpStatusCode.NotModified)
            return new RawResult { NotModified = true, FinalUri = finalUri, ETag = etag, LastModified = lastModified };
        if ((int)response.StatusCode >= 400)
            return new RawResult { Error = $"HTTP {(int)response.StatusCode}", FinalUri = finalUri };

        if (response.Content.Headers.ContentLength > MaxBytes)
            return new RawResult { Error = "TOO BIG", FinalUri = finalUri };

        var body = await ReadCappedAsync(response.Content, token).ConfigureAwait(false);
        if (body == null)
            return new RawResult { Error = "TOO BIG", FinalUri = finalUri };

        var newEtag = response.Headers.ETag?.ToString();
        var newLastModified = response.Content.Headers.LastModified?.ToString("R");

        if (FeedParser.LooksLikeHtml(body))
            return new RawResult { Html = body, FinalUri = finalUri };

        try
        {
            return new RawResult
            {
                Feed = FeedParser.Parse(body, finalUri),
                FinalUri = finalUri,
                ETag = newEtag,
                LastModified = newLastModified
            };
        }
        catch (FeedParseException ex)
        {
            return new RawResult { Error = ex.Code, FinalUri = finalUri };
        }
        catch (Exception)
        {
            // Feed content is untrusted: whatever else it trips over is a bad feed, not a crash.
            return new RawResult { Error = "BAD FEED", FinalUri = finalUri };
        }
    }

    /// <summary>Thumbnail bytes (at most 3 MB, 10 s); null on any failure — the panel just shows no image.</summary>
    public static async Task<byte[]?> DownloadImageAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || IsIntranetHost(uri)) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await ImageHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 3 * 1024 * 1024) return null;
            return await ReadCappedAsync(response.Content, timeout.Token, 3 * 1024 * 1024).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Bare single-label names (http://router/) are intranet names whatever they resolve to.</summary>
    private static bool IsIntranetHost(Uri uri)
        => uri.IsLoopback || uri.HostNameType == UriHostNameType.Dns && !uri.Host.TrimEnd('.').Contains('.');

    /// <summary>Races IPv6 and IPv4 so one broken family can't stall every fetch (see <see cref="HappyEyeballs"/>).</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        var socket = await HappyEyeballs.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// Connects only to public addresses: a feed shouldn't be able to make this
    /// PC send requests into its own network (router, NAS, cloud metadata) just
    /// by naming an image there, directly, through DNS or through a redirect.
    /// </summary>
    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        // Connect to exactly the addresses checked here, so a second lookup can't answer differently.
        var allowed = addresses.Where(IsPublicAddress).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"{context.DnsEndPoint.Host} is not a public address");

        var socket = await HappyEyeballs.ConnectAsync(allowed, context.DnsEndPoint.Port, token).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>False for loopback, private (RFC 1918 / ULA), link-local, CGNAT, multicast and reserved ranges.</summary>
    private static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6Multicast ||
                     b.Take(12).All(x => x == 0) ||                                  // IPv4-compatible (::a.b.c.d)
                     b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b);  // NAT64 64:ff9b::/96
        }
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||           // "this network", private, loopback, multicast/reserved
                 (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                 (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 169 && b[1] == 254) ||                                     // link-local, incl. cloud metadata
                 (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||                       // CGNAT
                 (b[0] == 198 && b[1] >= 18 && b[1] <= 19) ||                        // benchmarking
                 (b[0] == 192 && b[1] == 0 && b[2] == 0));                           // IETF protocol assignments
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken token, int maxBytes = MaxBytes)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>First &lt;link rel="alternate"&gt; with an RSS, Atom or JSON Feed type, resolved against the page.</summary>
    public static string? DiscoverFeed(byte[] html, Uri pageUri)
    {
        var text = Encoding.UTF8.GetString(html, 0, Math.Min(html.Length, 512 * 1024));
        string? best = null;
        foreach (Match tag in LinkTag().Matches(text))
        {
            var attrs = Attributes(tag.Value);
            if (!attrs.TryGetValue("rel", out var rel) ||
                !rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("alternate", StringComparer.OrdinalIgnoreCase))
                continue;
            if (!attrs.TryGetValue("type", out var type) || !attrs.TryGetValue("href", out var href)) continue;
            type = type.ToLowerInvariant();
            if (type is not ("application/rss+xml" or "application/atom+xml" or "application/feed+json" or "application/json"))
                continue;
            var resolved = FeedParser.ResolveUrl(WebUtility.HtmlDecode(href), pageUri);
            if (resolved == null) continue;
            // Prefer the site's main feed over per-post comment feeds.
            if (!resolved.Contains("comment", StringComparison.OrdinalIgnoreCase)) return resolved;
            best ??= resolved;
        }
        return best;
    }

    private static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in HtmlAttribute().Matches(tag))
            result.TryAdd(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value);
        return result;
    }

    private static string Classify(HttpRequestException ex)
    {
        if (ex.StatusCode is HttpStatusCode code) return $"HTTP {(int)code}";
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return "DNS";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return "REFUSED";
                case SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown }:
                    return "UNREACH";
                case SocketException { SocketErrorCode: SocketError.ConnectionReset }:
                    return "RESET";
                case AuthenticationException:
                    return ex.HttpRequestError == HttpRequestError.SecureConnectionError &&
                           e.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase) ? "CERT" : "TLS";
            }
        }
        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "DNS",
            HttpRequestError.SecureConnectionError => "TLS",
            HttpRequestError.ConnectionError => "CONN FAIL",
            _ => "FAIL"
        };
    }

    // NonBacktracking: linear time on hostile pages (thousands of unclosed "<link").
    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex LinkTag();

    [GeneratedRegex(@"([a-zA-Z-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+))", RegexOptions.NonBacktracking)]
    private static partial Regex HtmlAttribute();
}
