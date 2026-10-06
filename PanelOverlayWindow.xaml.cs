using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using NewsWatch.Models;
using NewsWatch.Services;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace NewsWatch;

/// <summary>
/// Borderless, transparent, topmost strip pinned to the left (or right, per
/// Settings) edge of the panel screen: the primary, or --monitor N. Hosts the
/// news panels so they appear even when the main window is minimised or
/// hidden to the tray.
/// </summary>
public partial class PanelOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private static readonly Brush TileBackground = Frozen(Color.FromRgb(0x10, 0x17, 0x26));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xD6, 0xE2, 0xF0));
    private static readonly Brush TextDimBrush = Frozen(Color.FromRgb(0x5E, 0x71, 0x91));
    private static readonly Brush ThumbBackground = Frozen(Color.FromRgb(0x0C, 0x12, 0x20));

    // Live panels per feed, so removing or pausing a feed clears its panels.
    private readonly Dictionary<Guid, List<Border>> _feedTiles = new();
    private readonly HashSet<Border> _leaving = new();

    /// <summary>A story waiting for room on screen. Its panel is built when shown, so "3 min ago" is current.</summary>
    private sealed class Pending
    {
        public required Guid FeedId { get; init; }
        public required Action Show { get; init; }
        public StoryEvent? Log { get; init; }
    }

    // Oldest first. New panels wait here instead of pushing older ones off before they've been read.
    private readonly List<Pending> _queue = new();

    // A burst (several stories in one check) slides in one at a time, not all at once.
    private readonly DispatcherTimer _pace = new() { Interval = TimeSpan.FromMilliseconds(450) };

    /// <summary>Left-click opens the story and right-click dismisses, instead of the other way round. Read at click time.</summary>
    public bool ClickOpensStory { get; set; }

    private int _maxOnScreen = 5;

    /// <summary>Beyond this many on screen, panels wait in the queue. Lowering it slides the oldest away.</summary>
    public int MaxOnScreen
    {
        get => _maxOnScreen;
        set
        {
            _maxOnScreen = value;
            TrimToMax();
            Pump();
        }
    }

    private int _maxQueued = 20;

    /// <summary>Most panels waiting for room; past it the oldest waiting story is skipped.</summary>
    public int MaxQueued
    {
        get => _maxQueued;
        set
        {
            _maxQueued = value;
            TrimQueue();
            UpdateQueueChip();
        }
    }

    private const double StripWidth = 420;
    private bool _onLeft = true;

    /// <summary>Dock to the left edge (default) or the right. Applies to panels already showing too.</summary>
    public bool OnLeft
    {
        get => _onLeft;
        set
        {
            if (_onLeft == value) return;
            _onLeft = value;
            // 16 px to the screen edge, the rest of the strip on the inner
            // side leaves room for the panels' glow.
            Column.HorizontalAlignment = value ? System.Windows.HorizontalAlignment.Left : System.Windows.HorizontalAlignment.Right;
            Column.Margin = value ? new Thickness(16, 16, 0, 16) : new Thickness(0, 16, 16, 16);
            PositionOnScreen();
        }
    }

    /// <summary>Where a panel sits before sliding in / after sliding out: just past the docked edge.</summary>
    private double OffscreenX => _onLeft ? -StripWidth : StripWidth;

    public PanelOverlayWindow()
    {
        InitializeComponent();
        _pace.Tick += (_, _) =>
        {
            _pace.Stop();
            Pump();
        };
        PositionOnScreen();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea))
                PositionOnScreen();
        };
        SourceInitialized += (_, _) =>
        {
            // Never steal focus, never show in Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            PositionOnScreen(); // re-run with the window's real device transform available
        };
    }

    private void PositionOnScreen()
    {
        Width = StripWidth;

        // --monitor N picks a specific screen (1-based); out of range -> primary.
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (App.MonitorOverride is int n && n >= 1 && n <= screens.Length)
        {
            // Screen gives device pixels; WPF wants DIPs. Prefer this window's
            // actual device transform (correct sign/scale for monitors left of
            // or above the primary); fall back to desktop DPI pre-Show.
            var wa = screens[n - 1].WorkingArea;
            double scale = PresentationSource.FromVisual(this)?.CompositionTarget
                ?.TransformFromDevice.M11 ?? GetDipScale();
            MaxHeight = wa.Height * scale;
            Left = _onLeft ? wa.Left * scale : (wa.Right * scale) - Width;
            Top = wa.Top * scale;
            return;
        }

        var area = SystemParameters.WorkArea;
        MaxHeight = area.Height;
        Left = _onLeft ? area.Left : area.Right - Width;
        Top = area.Top;
    }

    private static double GetDipScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return 96.0 / g.DpiX;
    }

    // ===== Panels =====

    /// <summary>
    /// Queue a story's panel: it slides in now if there's room, otherwise once a panel
    /// leaves. <paramref name="log"/>'s Shown follows it (QUEUED, then PANEL or SKIPPED).
    /// </summary>
    public void ShowStory(FeedEntry feed, FeedItem item, Color accent, int durationSeconds,
        bool showSummary, bool showImage, StoryEvent? log = null)
    {
        var feedId = feed.Id;
        var feedName = feed.Name;
        if (log != null) log.Shown = "QUEUED";
        _queue.Add(new Pending
        {
            FeedId = feedId,
            Log = log,
            Show = () => AddTile(feedId, accent, StoryBody(feedName, item, accent, showSummary, showImage), durationSeconds, item.Link)
        });
        Pump();
        TrimQueue();
        UpdateQueueChip();
    }

    /// <summary>Show the next waiting panel if there's room; the one after follows at least a beat later.</summary>
    private void Pump()
    {
        if (!_pace.IsEnabled && _queue.Count > 0 && LiveCount() < MaxOnScreen)
        {
            var next = _queue[0];
            _queue.RemoveAt(0);
            if (next.Log != null) next.Log.Shown = "PANEL";
            next.Show();
            _pace.Start(); // the rest of a batch is usually still being queued: keep a gap regardless
        }
        UpdateQueueChip();
    }

    private int LiveCount() => PanelHost.Children.OfType<Border>().Count(t => !_leaving.Contains(t));

    /// <summary>Skip the oldest waiting stories past MaxQueued; ones about to fill free slots don't count as waiting.</summary>
    private void TrimQueue()
    {
        var freeSlots = Math.Max(0, MaxOnScreen - LiveCount());
        while (_queue.Count > MaxQueued + freeSlots)
        {
            if (_queue[0].Log is { } log) log.Shown = "SKIPPED";
            _queue.RemoveAt(0);
        }
    }

    /// <summary>Drop every waiting panel, marking its story in History (SKIPPED, HELD ...).</summary>
    public void ClearQueue(string status)
    {
        foreach (var pending in _queue)
            if (pending.Log != null) pending.Log.Shown = status;
        _queue.Clear();
        UpdateQueueChip();
    }

    private void QueueChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => ClearQueue("SKIPPED");

    private void UpdateQueueChip()
    {
        // Stories about to slide into free slots aren't "waiting" as far as the reader is concerned.
        var waiting = _queue.Count - Math.Max(0, MaxOnScreen - LiveCount());
        QueueChip.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (waiting > 0)
            QueueText.Text = $"+{waiting} MORE WAITING";
    }

    private UIElement StoryBody(string feedName, FeedItem item, Color accent, bool showSummary, bool showImage)
    {
        var content = new StackPanel();
        content.Children.Add(HeaderRow(feedName, accent, StoryTimeText(item.Published)));
        content.Children.Add(new TextBlock
        {
            Text = item.Title,
            Foreground = TextBrush,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis,
            LineHeight = 19,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 19 * 3
        });
        if (showSummary && item.Summary.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = item.Summary,
                Foreground = TextDimBrush,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.WordEllipsis,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = 16 * 2
            });
        }

        UIElement body = content;
        if (showImage && item.ImageUrl != null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(content);
            var thumb = new Border
            {
                Width = 96,
                Height = 54,
                CornerRadius = new CornerRadius(3),
                Background = ThumbBackground,
                Margin = new Thickness(12, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed // until the image has actually loaded
            };
            Grid.SetColumn(thumb, 1);
            grid.Children.Add(thumb);
            _ = LoadThumbnailAsync(thumb, item.ImageUrl);
            body = grid;
        }
        return body;
    }

    private static Grid HeaderRow(string feedName, Color accent, string right)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = feedName.ToUpperInvariant(),
            Foreground = Frozen(accent),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var time = new TextBlock
        {
            Text = right,
            Foreground = TextDimBrush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(time, 1);
        header.Children.Add(time);
        return header;
    }

    /// <summary>"14:05 · 3 min ago" from the story's date; just the time now when the feed gives none.</summary>
    private static string StoryTimeText(DateTime? publishedUtc)
    {
        if (publishedUtc is not DateTime utc) return DateTime.Now.ToString("HH:mm");
        var local = utc.ToLocalTime();
        var age = DateTime.Now - local;
        var ago = age.TotalMinutes < 1 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalHours < 24 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} d ago";
        return age.TotalMinutes < -1 ? local.ToString("HH:mm") : $"{local:HH:mm} · {ago}";
    }

    private static async Task LoadThumbnailAsync(Border thumb, string url)
    {
        var bytes = await FeedFetcher.DownloadImageAsync(url);
        if (bytes == null) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 192; // 2x the thumb, sharp at 200% scaling
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            thumb.Visibility = Visibility.Visible;
        }
        catch
        {
            // Undecodable (e.g. WebP without the codec installed): no thumbnail.
        }
    }

    private void AddTile(Guid feedId, Color accent, UIElement body, int durationSeconds, string? link)
    {
        // Topmost can be silently lost (another topmost window asserting itself,
        // Explorer restart, resume from sleep) — re-assert it for every panel.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        var accentBrush = Frozen(accent);
        var tile = new Border
        {
            Background = TileBackground,
            BorderBrush = accentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 10),
            Cursor = Cursors.Hand,
            ToolTip = ClickTip(link),
            RenderTransform = new TranslateTransform(OffscreenX, 0),
            Opacity = 0,
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 20, ShadowDepth = 0, Opacity = 0.45 }
        };
        ToolTipService.SetInitialShowDelay(tile, 1500);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stripe = new Rectangle { Fill = accentBrush, RadiusX = 2, RadiusY = 2 };
        grid.Children.Add(stripe);

        var padded = new Border { Padding = new Thickness(14, 11, 14, 12), Child = body };
        Grid.SetColumn(padded, 1);
        grid.Children.Add(padded);
        tile.Child = grid;

        // One button dismisses, the other opens the story (http/https only) and dismisses;
        // which is which is a setting, so it's looked up at click time.
        void Click(bool open)
        {
            if (open) ValidationHelpers.OpenInBrowser(link);
            DismissTile(tile, feedId);
        }
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (!PanelTouch.FromTouch(e)) Click(open: ClickOpensStory);
        };
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!PanelTouch.FromTouch(e)) Click(open: !ClickOpensStory);
        };
        tile.ToolTipOpening += (_, _) => tile.ToolTip = ClickTip(link); // follows a settings change
        PanelHost.Children.Insert(0, tile);
        if (!IsVisible) Show(); // ShowActivated=false + WS_EX_NOACTIVATE: never steals focus

        if (!_feedTiles.TryGetValue(feedId, out var list))
            _feedTiles[feedId] = list = new List<Border>();
        list.Add(tile);

        var slideIn = new DoubleAnimation(OffscreenX, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
        tile.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);
        tile.BeginAnimation(OpacityProperty, fadeIn);

        // Hovering holds the panel so a headline can be finished; leaving gives it a few more seconds.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissTile(tile, feedId);
        };
        timer.Start();
        void Pause() => timer.Stop();
        void Resume()
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Min(durationSeconds, 5));
            timer.Start();
        }
        tile.MouseEnter += (_, e) => { if (!PanelTouch.FromTouch(e)) Pause(); };
        tile.MouseLeave += (_, e) => { if (!PanelTouch.FromTouch(e)) Resume(); };
        tile.Unloaded += (_, _) => timer.Stop();

        // Touch: tap and press-and-hold stand in for the two clicks; a swipe toward the edge throws it off.
        PanelTouch.Attach(tile, () => _onLeft,
            tap: () => Click(open: ClickOpensStory),
            hold: () => Click(open: !ClickOpensStory),
            swipedOff: () => DismissTile(tile, feedId),
            started: Pause, ended: Resume);
    }

    private string ClickTip(string? link)
        => link == null ? "Click or swipe to dismiss"
            : ClickOpensStory ? "Click to open the story · right-click or swipe to dismiss"
            : "Click or swipe to dismiss · right-click to open the story";

    /// <summary>Slide out the oldest (bottom) panels beyond MaxOnScreen.</summary>
    private void TrimToMax()
    {
        var live = PanelHost.Children.OfType<Border>().Where(t => !_leaving.Contains(t)).ToList();
        for (var i = live.Count - 1; i >= MaxOnScreen; i--)
            DismissTile(live[i]);
    }

    /// <summary>Remove all panels for a feed, waiting ones included (feed removed or paused).</summary>
    public void DismissTilesFor(Guid feedId)
    {
        foreach (var pending in _queue.Where(p => p.FeedId == feedId).ToList())
        {
            if (pending.Log != null) pending.Log.Shown = "SKIPPED";
            _queue.Remove(pending);
        }
        UpdateQueueChip();

        if (_feedTiles.Remove(feedId, out var tiles))
            foreach (var tile in tiles.ToList())
                DismissTile(tile);
    }

    private void DismissTile(Border tile, Guid? feedId = null)
    {
        if (!PanelHost.Children.Contains(tile) || !_leaving.Add(tile)) return;

        foreach (var (id, list) in _feedTiles.Where(kv => feedId == null || kv.Key == feedId).ToList())
        {
            if (list.Remove(tile) && list.Count == 0) _feedTiles.Remove(id);
        }

        // From wherever it is now: a swiped panel carries on from under the finger.
        var slideOut = new DoubleAnimation(OffscreenX, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
        fadeOut.Completed += (_, _) =>
        {
            PanelHost.Children.Remove(tile);
            _leaving.Remove(tile);
            if (PanelHost.Children.Count == 0) Hide(); // no panels = no window = no dead zone
        };
        tile.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideOut);
        tile.BeginAnimation(OpacityProperty, fadeOut);

        Pump(); // its slot is free: the next waiting panel slides in as this one slides out
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
