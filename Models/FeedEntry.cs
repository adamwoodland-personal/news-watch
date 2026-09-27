using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace NewsWatch.Models;

public enum FeedStatus
{
    Unknown,
    Ok,
    Error
}

public class FeedEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string _url = "";
    private int _intervalSeconds = 300;
    private bool _enabled = true;
    private bool _playSound = true;
    private string? _color;
    private string _includeKeywords = "";
    private string _excludeKeywords = "";
    private FeedStatus _status = FeedStatus.Unknown;
    private string? _lastError;
    private int _itemCount = -1;
    private string _format = "";
    private DateTime? _lastChecked;
    private DateTime? _lastNewStory;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>The feed itself (RSS, Atom, RDF or JSON Feed), not the website it belongs to.</summary>
    public string Url
    {
        get => _url;
        set { _url = value; OnPropertyChanged(); }
    }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set { _intervalSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(IntervalDisplay)); }
    }

    /// <summary>Unticked = feed is paused: no fetches, no panels.</summary>
    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDisplay)); }
    }

    public bool PlaySound
    {
        get => _playSound;
        set { _playSound = value; OnPropertyChanged(); }
    }

    /// <summary>Per-feed panel colour (hex); null = use the global setting.</summary>
    public string? Color
    {
        get => _color;
        set { _color = value; OnPropertyChanged(); }
    }

    /// <summary>Comma-separated; blank = every story. Otherwise a story needs at least one in its title or summary.</summary>
    public string IncludeKeywords
    {
        get => _includeKeywords;
        set { _includeKeywords = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilterDisplay)); }
    }

    /// <summary>Comma-separated; a story mentioning any of these is skipped.</summary>
    public string ExcludeKeywords
    {
        get => _excludeKeywords;
        set { _excludeKeywords = value; OnPropertyChanged(); OnPropertyChanged(nameof(FilterDisplay)); }
    }

    /// <summary>HTTP validators from the last 200 response, so unchanged feeds cost a 304.</summary>
    [JsonIgnore] public string? ETag { get; set; }
    [JsonIgnore] public string? LastModified { get; set; }

    [JsonIgnore]
    public FeedStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDisplay)); OnPropertyChanged(nameof(DetailDisplay)); }
    }

    /// <summary>Why the last fetch failed (TIMEOUT, HTTP 404, NOT A FEED, ...); null when OK.</summary>
    [JsonIgnore]
    public string? LastError
    {
        get => _lastError;
        set { _lastError = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailDisplay)); }
    }

    [JsonIgnore]
    public int ItemCount
    {
        get => _itemCount;
        set { _itemCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailDisplay)); }
    }

    /// <summary>RSS / ATOM / RDF / JSON, as detected on the last successful fetch.</summary>
    [JsonIgnore]
    public string Format
    {
        get => _format;
        set { _format = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailDisplay)); }
    }

    [JsonIgnore]
    public DateTime? LastChecked
    {
        get => _lastChecked;
        set { _lastChecked = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastCheckedDisplay)); }
    }

    [JsonIgnore]
    public DateTime? LastNewStory
    {
        get => _lastNewStory;
        set { _lastNewStory = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastCheckedDisplay)); }
    }

    private System.Windows.Media.Brush? _swatch;

    /// <summary>The panel colour actually used (own or global), for the list row. Set by MainWindow.</summary>
    [JsonIgnore]
    public System.Windows.Media.Brush? Swatch
    {
        get => _swatch;
        set { _swatch = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string StatusDisplay =>!Enabled ? "PAUSED" : Status switch
    {
        FeedStatus.Ok => "LIVE",
        FeedStatus.Error => "ERROR",
        _ => "PENDING"
    };

    [JsonIgnore]
    public string DetailDisplay =>
        Status == FeedStatus.Error && LastError != null ? LastError
        : Status == FeedStatus.Ok && ItemCount >= 0 ? $"{Format} · {ItemCount}"
        : "—";

    [JsonIgnore]
    public string LastCheckedDisplay => LastChecked?.ToString("HH:mm:ss") ?? "—";

    [JsonIgnore]
    public string IntervalDisplay => IntervalSeconds % 60 == 0 ? $"{IntervalSeconds / 60}m" : $"{IntervalSeconds}s";

    [JsonIgnore]
    public string FilterDisplay =>
        (IncludeKeywords.Trim().Length > 0 ? "+" : "") + (ExcludeKeywords.Trim().Length > 0 ? "−" : "");

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
