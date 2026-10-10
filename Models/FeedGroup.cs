namespace NewsWatch.Models;

/// <summary>
/// A user-made tab in the main list. Cosmetic only: it changes which rows show,
/// never how feeds are fetched or announced. Feeds point at it by Id, so renaming
/// is free. The Default tab isn't stored; feeds with no group belong to it.
/// </summary>
public class FeedGroup
{
    public const string DefaultName = "Default";
    public const int MaxNameLength = 40;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>Trimmed and capped; null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length > 0 ? clean : null;
    }

    /// <summary><paramref name="name"/>, or "name 2", "name 3" ... if a tab already has it (ignoring case).</summary>
    public static string Unique(string name, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(name)) return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (!set.Contains(candidate)) return candidate;
        }
    }
}
