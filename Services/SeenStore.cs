using System.IO;
using System.Text.Json;
using NewsWatch.Models;

namespace NewsWatch.Services;

/// <summary>
/// Which stories each feed has already produced, persisted next to
/// settings.json (seen.json) so a restart doesn't re-announce the whole feed
/// and stories published while the app was closed can be caught up on.
/// A feed with no record at all has never been fetched: its first fetch is
/// silent. UI thread only.
/// </summary>
public sealed class SeenStore
{
    // Two keys per story (id + link); comfortably more than any feed's window.
    private const int MinKeysPerFeed = 2000;

    private sealed class FeedKeys
    {
        public readonly List<string> Order = new();
        public readonly HashSet<string> Set = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<Guid, FeedKeys> _feeds = new();
    private bool _dirty;

    public static string FilePath => Path.Combine(SettingsService.Dir, "seen.json");

    public static SeenStore Load()
    {
        var store = new SeenStore();
        try
        {
            if (!File.Exists(FilePath)) return store;
            var data = JsonSerializer.Deserialize<Dictionary<Guid, List<string>>>(File.ReadAllText(FilePath));
            if (data == null) return store;
            foreach (var (id, keys) in data)
            {
                var entry = store.Entry(id);
                // Oversized keys come from before keys were capped: no item can produce them now.
                foreach (var key in keys ?? new List<string>())
                    if (key != null && key.Length <= FeedItem.MaxKeyChars && entry.Set.Add(key))
                        entry.Order.Add(key);
            }
        }
        catch
        {
            // Unreadable = start fresh: every feed's next fetch is silent, nothing floods.
            store._feeds.Clear();
        }
        return store;
    }

    public bool HasHistory(Guid feedId) => _feeds.ContainsKey(feedId);

    public bool IsSeen(Guid feedId, IEnumerable<string> keys)
        => _feeds.TryGetValue(feedId, out var entry) && keys.Any(entry.Set.Contains);

    /// <summary>Records every key of every item (see FeedItem.SeenKeysFor); creates the feed's record even for an empty feed.</summary>
    public void MarkSeen(Guid feedId, IReadOnlyCollection<string[]> itemKeys)
    {
        var entry = Entry(feedId);
        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in itemKeys.SelectMany(k => k))
        {
            current.Add(key);
            if (entry.Set.Add(key))
            {
                entry.Order.Add(key);
                _dirty = true;
            }
        }

        // Oldest first out, but never anything the feed still lists (a pinned
        // story listed for months would otherwise come back as "new").
        var cap = Math.Max(MinKeysPerFeed, current.Count * 2);
        if (entry.Order.Count <= cap) return;
        var excess = entry.Order.Count - cap;
        var kept = new List<string>(cap);
        foreach (var key in entry.Order)
        {
            if (excess > 0 && !current.Contains(key))
            {
                entry.Set.Remove(key);
                excess--;
            }
            else
            {
                kept.Add(key);
            }
        }
        entry.Order.Clear();
        entry.Order.AddRange(kept);
        _dirty = true;
    }

    /// <summary>Forget a feed (removed, or its URL changed): it starts again with a silent first fetch.</summary>
    public void Forget(Guid feedId)
    {
        if (_feeds.Remove(feedId)) _dirty = true;
    }

    /// <summary>Drop records for feeds that no longer exist (removed while the app was closed).</summary>
    public void Retain(IEnumerable<Guid> feedIds)
    {
        var keep = feedIds.ToHashSet();
        foreach (var id in _feeds.Keys.Where(id => !keep.Contains(id)).ToList())
        {
            _feeds.Remove(id);
            _dirty = true;
        }
    }

    private FeedKeys Entry(Guid feedId)
    {
        if (!_feeds.TryGetValue(feedId, out var entry))
        {
            _feeds[feedId] = entry = new FeedKeys();
            _dirty = true;
        }
        return entry;
    }

    /// <summary>Atomic write, only when something changed. Failure is non-fatal (worst case: a silent re-fetch).</summary>
    public void SaveIfDirty()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(SettingsService.Dir);
            var data = _feeds.ToDictionary(kv => kv.Key, kv => kv.Value.Order);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, FilePath, overwrite: true);
            _dirty = false;
        }
        catch
        {
            // keep _dirty so the next fetch retries
        }
    }
}
