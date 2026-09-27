using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NewsWatch.Models;
using NewsWatch.Services;
using WinForms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;

namespace NewsWatch;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<FeedEntry> _feeds = new();
    private readonly FeedMonitor _monitor = new();
    private readonly PanelOverlayWindow _overlay = new();
    private readonly StoryLog _stories = new();
    private readonly SeenStore _seen = SeenStore.Load();
    private HistoryWindow? _historyWindow;
    private readonly WinForms.NotifyIcon _trayIcon;
    private AppSettings _settings = new();
    private bool _exiting;
    private bool _exitRequested;

    // Feeds fetched at least once this session: their next fetch is steady-state, not catch-up.
    private readonly HashSet<Guid> _fetchedThisSession = new();

    // Links announced recently by any feed, so a story carried by two feeds
    // (e.g. BBC top stories and BBC World) pops up once.
    private readonly Dictionary<string, (Guid FeedId, DateTime Seen)> _recentLinks = new();
    private static readonly TimeSpan RecentLinkWindow = TimeSpan.FromHours(12);

    public MainWindow()
    {
        InitializeComponent();

        Icon = AppIcon.WindowIcon;

        _settings = SettingsService.Load(out var loadWarning);
        ApplyOverlaySettings();

        // Subscribe before any loop starts so a fast first fetch can't
        // publish into the void.
        _monitor.FetchCompleted += OnFetchCompleted;

        foreach (var feed in _settings.Feeds)
        {
            _feeds.Add(feed);
            if (feed.Enabled)
                _monitor.Start(feed);
        }
        // Only prune seen history against a settings file that loaded cleanly:
        // after a failed load the feed list is the default one, not the user's.
        if (loadWarning == null)
            _seen.Retain(_feeds.Select(f => f.Id));
        RefreshSwatches();

        if (loadWarning != null)
        {
            Loaded += (_, _) => System.Windows.MessageBox.Show(this, loadWarning,
                "NEWS//WATCH", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        FeedList.ItemsSource = _feeds;
        FeedList.SelectionChanged += (_, _) =>
        {
            var hasSelection = FeedList.SelectedItem != null;
            EditButton.IsEnabled = hasSelection;
            RemoveButton.IsEnabled = hasSelection;
        };
        _feeds.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();

        _trayIcon = CreateTrayIcon();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                HideToTray();
        };

        // The overlay shows itself on the first panel and hides when empty,
        // so it's never on screen (or in the way) while there's no news.

        Closing += (_, e) =>
        {
            // X minimises to tray when enabled; only the tray's Exit really quits.
            if (_settings.CloseToTray && !_exitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            if (_settings.ConfirmOnExit)
            {
                // Owner only when visible — exiting from the tray has a hidden window.
                var answer = IsVisible
                    ? System.Windows.MessageBox.Show(this,
                        "Stop watching feeds and exit NEWS//WATCH?", "NEWS//WATCH",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
                    : System.Windows.MessageBox.Show(
                        "Stop watching feeds and exit NEWS//WATCH?", "NEWS//WATCH",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    _exitRequested = false;
                    return;
                }
            }

            _exiting = true;
            _monitor.Dispose();
            SaveSettings();
            _seen.SaveIfDirty();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _overlay.Close();
        };
    }

    // ===== Tray =====

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("History", null, (_, _) => { RestoreFromTray(); ShowHistory(); });
        menu.Items.Add("Check all feeds now", null, (_, _) => _monitor.CheckAllNow());

        var mute = new WinForms.ToolStripMenuItem("Mute sounds")
        {
            CheckOnClick = true,
            Checked = _settings.MuteSounds
        };
        mute.CheckedChanged += (_, _) =>
        {
            _settings.MuteSounds = mute.Checked;
            SaveSettings();
        };
        menu.Items.Add(mute);
        menu.Items.Add(BuildSuppressMenu());

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exitRequested = true; Close(); });

        var icon = new WinForms.NotifyIcon
        {
            Icon = AppIcon.CreateTrayIcon(),
            Text = $"NEWS//WATCH {VersionTag}",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => RestoreFromTray();
        return icon;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ===== Panel suppression (session-only) =====

    private DateTime? _suppressUntil; // null = off, MaxValue = until re-enabled
    private WinForms.ToolStripMenuItem? _suppressMenu;
    private WinForms.ToolStripMenuItem? _suppressOffItem;

    private bool PanelsSuppressed => _suppressUntil is { } until && DateTime.Now < until;

    private WinForms.ToolStripMenuItem BuildSuppressMenu()
    {
        _suppressMenu = new WinForms.ToolStripMenuItem("Hold panels");
        _suppressOffItem = AddSuppressOption("Off — show panels", null);
        _suppressOffItem.Checked = true;
        AddSuppressOption("Until re-enabled", TimeSpan.MaxValue);
        foreach (var (label, minutes) in new[]
                 { ("For 15 minutes", 15), ("For 30 minutes", 30), ("For 1 hour", 60),
                   ("For 2 hours", 120), ("For 12 hours", 720) })
            AddSuppressOption(label, TimeSpan.FromMinutes(minutes));

        // A timed hold may have lapsed since the menu was last open —
        // snap the checkmark and title back to reality before showing it.
        _suppressMenu.DropDownOpening += (_, _) =>
        {
            if (!PanelsSuppressed && _suppressUntil != null)
            {
                _suppressUntil = null;
                CheckOnlySuppressItem(_suppressOffItem!);
            }
            RefreshSuppressTitle();
        };
        return _suppressMenu;
    }

    private WinForms.ToolStripMenuItem AddSuppressOption(string label, TimeSpan? duration)
    {
        var item = new WinForms.ToolStripMenuItem(label);
        item.Click += (_, _) =>
        {
            _suppressUntil = duration == null ? null
                : duration == TimeSpan.MaxValue ? DateTime.MaxValue
                : DateTime.Now + duration.Value;
            CheckOnlySuppressItem(item);
            RefreshSuppressTitle();
            if (duration != null) _overlay.ClearQueue("HELD"); // waiting panels are held too
        };
        _suppressMenu!.DropDownItems.Add(item);
        return item;
    }

    private void CheckOnlySuppressItem(WinForms.ToolStripMenuItem selected)
    {
        foreach (WinForms.ToolStripMenuItem item in _suppressMenu!.DropDownItems)
            item.Checked = ReferenceEquals(item, selected);
    }

    private void RefreshSuppressTitle()
    {
        _suppressMenu!.Text = _suppressUntil switch
        {
            null => "Hold panels",
            { } u when u == DateTime.MaxValue => "Hold panels (until re-enabled)",
            { } u => $"Hold panels (until {u:HH:mm})"
        };
    }

    private bool _trayShowsAlert;

    // Version in the tooltip so it's always obvious WHICH build is running.
    private static readonly string VersionTag =
        $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";

    /// <summary>Red page while any active feed is failing, cyan otherwise.</summary>
    private void UpdateTrayIcon()
    {
        var failing = _feeds.Count(f => f.Enabled && f.Status == FeedStatus.Error);
        var alert = failing > 0;
        _trayIcon.Text = alert
            ? $"NEWS//WATCH {VersionTag} — {failing} feed{(failing == 1 ? "" : "s")} failing"
            : $"NEWS//WATCH {VersionTag} — watching {_feeds.Count(f => f.Enabled)}";
        if (alert == _trayShowsAlert) return;

        _trayShowsAlert = alert;
        var old = _trayIcon.Icon;
        _trayIcon.Icon = AppIcon.CreateTrayIcon(alert);
        old?.Dispose();
    }

    private void HideToTray() => Hide(); // to tray; the overlay keeps showing panels

    private void ApplyOverlaySettings()
    {
        _overlay.OnLeft = _settings.PanelSide == PanelSide.Left;
        _overlay.ClickOpensStory = _settings.ClickOpensStory;
        _overlay.MaxOnScreen = _settings.MaxPanelsOnScreen;
        _overlay.MaxQueued = _settings.MaxQueuedPanels;
    }

    private Color GlobalColor =>
        ValidationHelpers.ParseColor(_settings.PanelColor) ?? Color.FromRgb(0xFF, 0xB0, 0x20);

    /// <summary>The feed's own colour, else the global one.</summary>
    private Color ColorFor(FeedEntry feed)
        => (feed.Color is string own ? ValidationHelpers.ParseColor(own) : null) ?? GlobalColor;

    private void RefreshSwatches()
    {
        foreach (var feed in _feeds)
        {
            var brush = new SolidColorBrush(ColorFor(feed));
            brush.Freeze();
            feed.Swatch = brush;
        }
    }

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory();

    /// <summary>One history window at a time; a second request brings it forward.</summary>
    private void ShowHistory()
    {
        if (_historyWindow != null)
        {
            if (_historyWindow.WindowState == WindowState.Minimized) _historyWindow.WindowState = WindowState.Normal;
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(_stories) { Owner = this };
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ApplyOverlaySettings();
            RefreshSwatches();
            SaveSettings();
        }
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e) => _monitor.CheckAllNow();

    // ===== State =====

    private void UpdateEmptyState()
        => EmptyState.Visibility = _feeds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void SaveSettings()
    {
        UpdateTrayIcon(); // list or status just changed (remove/pause/edit)
        _settings.Feeds = _feeds.ToList();
        if (!SettingsService.Save(_settings))
        {
            _trayIcon?.ShowBalloonTip(3000, "NEWS//WATCH",
                "Settings could not be saved — recent changes may be lost on exit.",
                WinForms.ToolTipIcon.Warning);
        }
    }

    // ===== CRUD =====

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new EditFeedWindow(_settings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            var feed = dialog.Result;
            _feeds.Add(feed);
            RefreshSwatches();
            if (feed.Enabled)
                _monitor.Start(feed);
            SaveSettings();
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditSelected();

    private static System.Windows.Controls.ListBoxItem? FindListBoxItem(object? source)
    {
        var element = source as DependencyObject;
        while (element != null && element is not System.Windows.Controls.ListBoxItem)
        {
            element = element is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as System.Windows.Controls.ListBoxItem;
    }

    // Double-click a row = edit that row; double-click empty space = add.
    // (Selection alone can't tell these apart — a selected row stays selected
    // when you click the background.)
    private void FeedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindListBoxItem(e.OriginalSource) is { DataContext: FeedEntry feed })
        {
            FeedList.SelectedItem = feed;
            EditSelected();
        }
        else
        {
            Add_Click(sender, e);
        }
    }

    // ===== Drag-to-reorder =====

    private Point _dragStart;
    private FeedEntry? _dragCandidate;

    private void FeedList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragCandidate = FindListBoxItem(e.OriginalSource)?.DataContext as FeedEntry;
    }

    private void FeedList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;

        // Don't hijack plain clicks/double-clicks: require a real drag distance.
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(FeedList, dragged, DragDropEffects.Move);
    }

    private void FeedList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(FeedEntry)) is not FeedEntry dragged) return;
        var oldIndex = _feeds.IndexOf(dragged);
        if (oldIndex < 0) return;

        int insertAt;
        var targetItem = FindListBoxItem(e.OriginalSource);
        if (targetItem?.DataContext is FeedEntry target && !ReferenceEquals(target, dragged))
        {
            // Above the row's midpoint = before it, below = after it.
            insertAt = _feeds.IndexOf(target);
            if (e.GetPosition(targetItem).Y > targetItem.ActualHeight / 2) insertAt++;
            if (insertAt > oldIndex) insertAt--; // account for removal of the dragged row
        }
        else
        {
            insertAt = _feeds.Count - 1; // dropped on empty space: move to end
        }

        if (insertAt != oldIndex)
        {
            _feeds.Move(oldIndex, insertAt);
            SaveSettings();
        }
    }

    private void EditSelected()
    {
        if (FeedList.SelectedItem is not FeedEntry feed) return;

        var dialog = new EditFeedWindow(_settings, feed) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (dialog.RemoveRequested)
        {
            RemoveFeed(feed);
            return;
        }
        if (dialog.Result is not { } result) return;

        _monitor.Stop(feed.Id); // stop the old loop before mutating shared state
        if (!string.Equals(feed.Url, result.Url, StringComparison.Ordinal))
        {
            // A different feed: its first fetch is silent again, like a new one.
            _seen.Forget(feed.Id);
            _fetchedThisSession.Remove(feed.Id);
            feed.ETag = null;
            feed.LastModified = null;
        }
        feed.Name = result.Name;
        feed.Url = result.Url;
        feed.IntervalSeconds = result.IntervalSeconds;
        feed.PlaySound = result.PlaySound;
        feed.Color = result.Color;
        feed.IncludeKeywords = result.IncludeKeywords;
        feed.ExcludeKeywords = result.ExcludeKeywords;
        feed.Enabled = result.Enabled;
        if (!feed.Enabled)
            _fetchedThisSession.Remove(feed.Id); // resuming later gets the catch-up treatment, like a relaunch
        feed.Status = FeedStatus.Unknown;
        feed.LastError = null;
        feed.ItemCount = -1;
        RefreshSwatches();
        if (feed.Enabled)
            _monitor.Start(feed); // fresh loop with new parameters
        else
            _overlay.DismissTilesFor(feed.Id);
        SaveSettings();
        _seen.SaveIfDirty();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (FeedList.SelectedItem is FeedEntry feed)
            RemoveFeed(feed);
    }

    private void RemoveFeed(FeedEntry feed)
    {
        _monitor.Stop(feed.Id);
        _feeds.Remove(feed);
        _overlay.DismissTilesFor(feed.Id);
        _seen.Forget(feed.Id);
        _fetchedThisSession.Remove(feed.Id);
        SaveSettings();
        _seen.SaveIfDirty();
    }

    private void ConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(SettingsService.Dir);
        System.Diagnostics.Process.Start("explorer.exe", SettingsService.Dir);
    }

    // ===== New stories =====

    private void OnFetchCompleted(object? sender, FetchCompletedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (_exiting) return;

            // Drop results from loops that were stopped/replaced (feed removed,
            // edited, or paused) between publication and this marshal.
            if (!_monitor.IsCurrent(e.Feed.Id, e.Token)) return;

            try
            {
                ProcessFetch(e.Feed, e.Result);
            }
            catch (Exception ex)
            {
                // Never let one odd story take the feed's loop down with it.
                e.Feed.Status = FeedStatus.Error;
                e.Feed.LastError = "APP ERROR";
                App.LogError($"Processing \"{e.Feed.Name}\" ({e.Feed.Url})", ex);
            }
            UpdateTrayIcon();
        });
    }

    private void ProcessFetch(FeedEntry feed, FetchResult result)
    {
        feed.LastChecked = DateTime.Now;
        if (result.Outcome == FetchOutcome.Error)
        {
            feed.LastError = result.Error;
            feed.Status = FeedStatus.Error;
            return;
        }

        feed.LastError = null;
        feed.Status = FeedStatus.Ok;
        if (result.Outcome == FetchOutcome.NotModified || result.Feed is not { } parsed) return;

        feed.Format = parsed.Format;
        feed.ItemCount = parsed.Items.Count;

        var firstEver = !_seen.HasHistory(feed.Id);
        var catchUp = !_fetchedThisSession.Contains(feed.Id); // first fetch since launch (or since the URL changed)
        var keys = FeedItem.SeenKeysFor(parsed.Items);

        // A feed never fetched before: everything in it is "old news" — just remember it.
        var stories = firstEver ? new List<FeedItem>() : SelectStories(feed, FreshItems(feed.Id, parsed.Items, keys));

        // Commit only once the batch is worked out, so a failure above leaves it to be
        // retried on the next fetch. History and panels below then happen at most once:
        // retrying after a failure there would repeat panels that were already shown.
        _fetchedThisSession.Add(feed.Id);
        _seen.MarkSeen(feed.Id, keys);
        _seen.SaveIfDirty();
        if (stories.Count == 0) return;

        feed.LastNewStory = DateTime.Now;
        var color = ColorFor(feed);
        var held = PanelsSuppressed;
        var toShow = held ? 0 : catchUp ? Math.Min(_settings.CatchUpPerFeed, stories.Count) : stories.Count;

        // Oldest first: History (newest on top) reads in story order, and panels queue
        // in that order so the newest ends up on top of the stack. When the screen is
        // full they wait their turn (the overlay moves each from QUEUED to PANEL).
        for (var i = stories.Count - 1; i >= 0; i--)
        {
            var logged = _stories.Record(feed, stories[i], color, held ? "HELD" : i < toShow ? "QUEUED" : "CATCH-UP");
            if (i < toShow)
                _overlay.ShowStory(feed, stories[i], color, _settings.PanelDurationSeconds,
                    _settings.ShowSummary, _settings.ShowImages, logged);
        }

        if (toShow == 0) return;

        if (feed.PlaySound && !_settings.MuteSounds)
            SoundService.PlayNews(); // once per batch, not per panel
    }

    /// <summary>
    /// Items not seen before, each story once: a feed that lists the same entry
    /// twice in one response (same guid) would otherwise get two panels.
    /// </summary>
    private List<FeedItem> FreshItems(Guid feedId, IReadOnlyList<FeedItem> items, IReadOnlyList<string[]> keys)
    {
        var inBatch = new HashSet<string>(StringComparer.Ordinal);
        var fresh = new List<FeedItem>();
        for (var i = 0; i < items.Count; i++)
        {
            if (_seen.IsSeen(feedId, keys[i])) continue;
            var duplicate = keys[i].Any(inBatch.Contains);
            inBatch.UnionWith(keys[i]);
            if (!duplicate) fresh.Add(items[i]);
        }
        return fresh;
    }

    /// <summary>Fresh items that pass the age, keyword and cross-feed filters, newest first.</summary>
    private List<FeedItem> SelectStories(FeedEntry feed, List<FeedItem> fresh)
    {
        if (fresh.Count == 0) return fresh;

        var cutoff = _settings.MaxStoryAgeHours > 0 ? DateTime.UtcNow.AddHours(-_settings.MaxStoryAgeHours) : DateTime.MinValue;
        var include = Keywords(feed.IncludeKeywords);
        var exclude = Keywords(feed.ExcludeKeywords);
        PruneRecentLinks();

        var stories = new List<FeedItem>();
        foreach (var item in fresh)
        {
            if (item.Published is DateTime published && published < cutoff) continue;
            var text = item.Title + " " + item.Summary;
            if (include.Count > 0 && !include.Any(k => k.IsMatch(text))) continue;
            if (exclude.Any(k => k.IsMatch(text))) continue;
            if (FeedItem.NormalizeLink(item.Link) is string link)
            {
                if (_recentLinks.TryGetValue(link, out var earlier) && earlier.FeedId != feed.Id)
                    continue; // another feed already announced it
                _recentLinks[link] = (feed.Id, DateTime.UtcNow);
            }
            stories.Add(item);
        }

        // Newest first (feeds without dates keep their own order, which is nearly always newest first).
        return stories.Select((item, index) => (item, index))
            .OrderByDescending(x => x.item.Published ?? DateTime.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }

    private void PruneRecentLinks()
    {
        var cutoff = DateTime.UtcNow - RecentLinkWindow;
        foreach (var key in _recentLinks.Where(kv => kv.Value.Seen < cutoff).Select(kv => kv.Key).ToList())
            _recentLinks.Remove(key);
    }

    /// <summary>Comma-separated keywords as case-insensitive whole-word matchers ("art" won't match "start").</summary>
    private static List<Regex> Keywords(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(k => new Regex(
                  (char.IsLetterOrDigit(k[0]) ? @"\b" : "") + Regex.Escape(k) + (char.IsLetterOrDigit(k[^1]) ? @"\b" : ""),
                  RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
              .ToList();
}
