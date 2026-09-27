namespace NewsWatch.Models;

/// <summary>One story as parsed from a feed. Plain text only: HTML is stripped by the parser.</summary>
public sealed class FeedItem
{
    /// <summary>guid / id / link, whichever the feed provides first; never empty.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Absolute http(s) link to the story, or null when the feed doesn't give one.</summary>
    public string? Link { get; init; }

    public string Summary { get; init; } = "";

    /// <summary>UTC; null when the feed has no (parseable) date.</summary>
    public DateTime? Published { get; init; }

    /// <summary>Absolute http(s) thumbnail URL (media:thumbnail, media:content, image enclosure), or null.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>
    /// Keys under which this story counts as "seen": its id and its link with
    /// tracking parameters and fragment dropped. Feeds like the BBC's bump the guid ("…#1" to
    /// "…#2") when a story is edited; matching on either key stops an edit
    /// from popping the same story again.
    /// </summary>
    /// <param name="includeLink">False when other items in the same fetch share this
    /// link (changelogs "…#v2"/"…#v3", everything pointing at the homepage):
    /// then the link identifies nothing and only the id counts.</param>
    public IEnumerable<string> SeenKeys(bool includeLink = true)
    {
        yield return Id;
        if (includeLink && NormalizeLink(Link) is string link && CompactKey(link) is var key && key != Id)
            yield return key;
    }

    /// <summary>Longest seen key kept as-is; anything longer is stored as its hash.</summary>
    public const int MaxKeyChars = 256;

    /// <summary>
    /// The key itself, or a fixed-size hash of it when it's longer than MaxKeyChars:
    /// keys are only compared, and feed-supplied ids can be arbitrarily large.
    /// </summary>
    public static string CompactKey(string key)
        => key.Length <= MaxKeyChars ? key
            : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    /// <summary>Seen keys for every item of one fetch, links used only where they're unique within it.</summary>
    public static List<string[]> SeenKeysFor(IReadOnlyList<FeedItem> items)
    {
        var linkCounts = items.Select(i => NormalizeLink(i.Link)).OfType<string>()
            .GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        return items.Select(i => i.SeenKeys(NormalizeLink(i.Link) is string l && linkCounts[l] == 1).ToArray()).ToList();
    }

    /// <summary>
    /// Link minus fragment and tracking parameters (utm_*, at_*, fbclid, ...).
    /// Other query parameters stay: on many sites (?p=123, item?id=…) they ARE the story.
    /// Only scheme and host are case-folded (Uri does that); path and query can be
    /// case-sensitive, and "/a" vs "/a/" can be different pages, so both stay exact.
    /// </summary>
    public static string? NormalizeLink(string? link)
    {
        if (link == null || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        var kept = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsTrackingParam(p.Split('=')[0]));
        var query = string.Join('&', kept);
        var path = uri.GetLeftPart(UriPartial.Path);
        return query.Length > 0 ? path + "?" + query : path;
    }

    private static bool IsTrackingParam(string name)
    {
        name = name.ToLowerInvariant();
        return name.StartsWith("utm_") || name.StartsWith("at_") ||
               name is "fbclid" or "gclid" or "ocid" or "cmpid" or "ns_mchannel" or "ns_source" or "ns_campaign";
    }
}
