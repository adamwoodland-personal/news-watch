using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using NewsWatch.Models;
using Color = System.Windows.Media.Color;

namespace NewsWatch.Services;

/// <summary>One new story. A snapshot, so it outlives edits to (or removal of) its feed.</summary>
public sealed class StoryEvent : INotifyPropertyChanged
{
    public required DateTime Time { get; init; }
    public required string Feed { get; init; }
    public required string Title { get; init; }
    public string? Link { get; init; }
    public string Summary { get; init; } = "";

    /// <summary>Local time; null when the feed gave no date.</summary>
    public DateTime? Published { get; init; }

    /// <summary>The feed's panel colour at the time, for the history row.</summary>
    public required System.Windows.Media.Brush FeedBrush { get; init; }

    private string _shown = "";

    /// <summary>
    /// "PANEL" when it popped up; "QUEUED" while waiting for room on screen; else why not:
    /// SKIPPED (dropped from the queue), CATCH-UP (over the catch-up limit), HELD (panels on hold).
    /// </summary>
    public required string Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shown)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TimeDisplay => Time.ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture);
    public string PublishedDisplay => Published?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "—";
    public bool HasLink => Link != null;
}

/// <summary>
/// In-memory list of every new story since the app started, newest first,
/// whether or not it got a panel. Not persisted. UI thread only.
/// </summary>
public sealed class StoryLog
{
    private const int MaxEvents = 5_000;

    public DateTime Started { get; } = DateTime.Now;

    /// <summary>Newest first, for binding.</summary>
    public ObservableCollection<StoryEvent> Events { get; } = new();

    /// <summary>A story's Shown changed after it was recorded (a queued story got its panel or was skipped).</summary>
    public event EventHandler? StatusChanged;

    public StoryEvent Record(FeedEntry feed, FeedItem item, Color color, string shown)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var story = new StoryEvent
        {
            Time = DateTime.Now,
            Feed = feed.Name,
            Title = item.Title,
            Link = item.Link,
            Summary = item.Summary,
            Published = item.Published?.ToLocalTime(),
            FeedBrush = brush,
            Shown = shown
        };
        story.PropertyChanged += (_, _) => StatusChanged?.Invoke(this, EventArgs.Empty);
        Events.Insert(0, story);
        while (Events.Count > MaxEvents)
            Events.RemoveAt(Events.Count - 1);
        return story;
    }

    /// <summary>Oldest first: Date, Time, Feed, Published, Title, Link, Shown.</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder("Date,Time,Feed,Published,Title,Link,Shown\r\n");
        for (var i = Events.Count - 1; i >= 0; i--)
        {
            var e = Events[i];
            sb.Append(e.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(',')
              .Append(CsvField(e.Feed)).Append(',')
              .Append(e.Published?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(CsvField(e.Title)).Append(',')
              .Append(CsvField(e.Link ?? "")).Append(',')
              .Append(e.Shown).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        // Titles come from the internet: stop one like "=HYPERLINK(...)" from
        // running as a formula when the file is opened in Excel.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
