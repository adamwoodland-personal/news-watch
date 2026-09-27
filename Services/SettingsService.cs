using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NewsWatch.Models;

namespace NewsWatch.Services;

/// <summary>Which edge of the panel screen the news panels appear on.</summary>
public enum PanelSide
{
    Left,
    Right
}

public class AppSettings
{
    public const string DefaultPanelColor = "#FFB020";

    public int PanelDurationSeconds { get; set; } = 15;

    /// <summary>Left by default so panels don't collide with PULSE//WATCH's tiles on the right.</summary>
    public PanelSide PanelSide { get; set; } = PanelSide.Left;

    /// <summary>False: left-click dismisses a panel, right-click opens its story. True: the other way round.</summary>
    public bool ClickOpensStory { get; set; }

    public bool CloseToTray { get; set; } = false;
    public bool ConfirmOnExit { get; set; } = true;
    public bool MuteSounds { get; set; }
    public string PanelColor { get; set; } = DefaultPanelColor;

    /// <summary>Beyond this many on screen, new panels wait in a queue until one leaves.</summary>
    public int MaxPanelsOnScreen { get; set; } = 5;

    /// <summary>Most panels waiting for room; past it the oldest waiting story is skipped (it stays in History).</summary>
    public int MaxQueuedPanels { get; set; } = 20;

    /// <summary>Stories published while the app was closed: panels per feed on the first check after launch. 0 = none.</summary>
    public int CatchUpPerFeed { get; set; } = 3;

    /// <summary>Skip stories older than this (feeds that re-surface old items). 0 = no limit.</summary>
    public int MaxStoryAgeHours { get; set; } = 24;

    public bool ShowSummary { get; set; } = true;
    public bool ShowImages { get; set; } = true;

    public List<FeedEntry> Feeds { get; set; } = new();
}

public static class SettingsService
{
    public static string Dir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewsWatch");

    public static string FilePath { get; private set; } = Path.Combine(Dir, "settings.json");

    /// <summary>Use a different settings file (the --settings switch). Call before anything loads.</summary>
    public static void UseFile(string path)
    {
        FilePath = Path.GetFullPath(path);
        Dir = Path.GetDirectoryName(FilePath) ?? Dir;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <param name="warning">Set when the settings file existed but could not be used;
    /// the damaged file is preserved as settings.json.corrupt.</param>
    public static AppSettings Load(out string? warning)
    {
        warning = null;
        if (File.Exists(FilePath))
        {
            string? json = null;
            try
            {
                json = ReadWithRetry(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or unreadable, not damaged: run on defaults but never save
                // over the real file this session.
                SavingDisabled = true;
                warning = $"The settings file couldn't be opened ({ex.Message}). Running with the default feed for now; changes won't be saved this session. Restart NEWS//WATCH to try again.";
                return Defaults();
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded != null)
                {
                    var problems = new List<string>();
                    var result = Sanitize(loaded, problems);
                    if (problems.Count > 0)
                        warning = string.Join(Environment.NewLine + Environment.NewLine, problems);
                    return result;
                }
            }
            catch
            {
                // fall through to backup + defaults
            }

            try
            {
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
            }
            catch
            {
                // best effort
            }
            warning = $"The settings file could not be read and has been backed up as settings.json.corrupt in {Dir}. Starting with defaults.";
        }

        return Defaults();
    }

    /// <summary>First run (or unreadable file): one starter feed.</summary>
    private static AppSettings Defaults() => new()
    {
        Feeds =
        {
            new FeedEntry { Name = "BBC News", Url = "https://feeds.bbci.co.uk/news/rss.xml" }
        }
    };

    /// <summary>True after the settings file couldn't be opened: Save refuses rather than overwrite it with defaults.</summary>
    public static bool SavingDisabled { get; private set; }

    /// <summary>Antivirus and sync clients (OneDrive) hold files briefly; give them a moment.</summary>
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>
    /// Clamps and repairs hand-edited or partially damaged values so they can't
    /// crash the app. Anything that can't be repaired is paused and described in
    /// <paramref name="problems"/>.
    /// </summary>
    private static AppSettings Sanitize(AppSettings s, List<string> problems)
    {
        s.PanelDurationSeconds = Math.Clamp(s.PanelDurationSeconds, 1, 3600);
        if (!Enum.IsDefined(s.PanelSide)) s.PanelSide = PanelSide.Left; // e.g. a hand-edited number
        if (ValidationHelpers.ParseColor(s.PanelColor ?? "") == null) s.PanelColor = AppSettings.DefaultPanelColor;
        s.MaxPanelsOnScreen = Math.Clamp(s.MaxPanelsOnScreen, 1, 20);
        s.MaxQueuedPanels = Math.Clamp(s.MaxQueuedPanels, 0, 100);
        s.CatchUpPerFeed = Math.Clamp(s.CatchUpPerFeed, 0, 20);
        s.MaxStoryAgeHours = Math.Clamp(s.MaxStoryAgeHours, 0, 24 * 30);

        s.Feeds ??= new List<FeedEntry>();
        s.Feeds.RemoveAll(f => f == null);

        var seenIds = new HashSet<Guid>();
        foreach (var f in s.Feeds)
        {
            if (f.Id == Guid.Empty || !seenIds.Add(f.Id))
            {
                f.Id = Guid.NewGuid(); // duplicate ids would cross-wire loops and seen-story history
                seenIds.Add(f.Id);
            }
            f.Name ??= "";
            f.IncludeKeywords ??= "";
            f.ExcludeKeywords ??= "";
            if (ValidationHelpers.NormalizeFeedUrl(f.Url) is string url)
            {
                f.Url = url;
            }
            else
            {
                if (f.Enabled)
                    problems.Add($"Feed \"{f.Name}\" has an invalid URL (\"{f.Url}\"), so it has been paused. Edit it to fix.");
                f.Url ??= "";
                f.Enabled = false;
            }
            f.IntervalSeconds = Math.Clamp(f.IntervalSeconds, 60, 86400);
            if (f.Color != null && ValidationHelpers.ParseColor(f.Color) == null) f.Color = null;
        }
        return s;
    }

    /// <summary>Atomic write (temp file + rename); returns false if saving failed.</summary>
    public static bool Save(AppSettings settings)
    {
        if (SavingDisabled) return false;
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
