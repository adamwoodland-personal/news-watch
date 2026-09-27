using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using NewsWatch.Models;
using NewsWatch.Services;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;

namespace NewsWatch;

public partial class EditFeedWindow : Window
{
    public FeedEntry? Result { get; private set; }

    /// <summary>True when the dialog closed via REMOVE (edit mode only); Result is null then.</summary>
    public bool RemoveRequested { get; private set; }

    private static readonly (string Name, string Hex)[] Presets =
    {
        ("Amber", "#FFB020"), ("Cyan", "#00E5FF"), ("Green", "#22E584"), ("Red", "#FF3B5C"),
        ("Magenta", "#FF4FD8"), ("Violet", "#9B7BFF"), ("Blue", "#3D8BFF"), ("White", "#D6E2F0")
    };

    private readonly string _globalColor;
    private readonly string? _originalUrl;
    private readonly DispatcherTimer _removeArmTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _closing = new();

    // The URL (as typed) that last passed TEST, and the feed URL it resolved to.
    private string? _testedInput;
    private string? _testedFeedUrl;
    private string? _feedTitle;

    // Set after a SAVE whose check failed; a second SAVE of the same URL goes through.
    private string? _saveAnywayUrl;

    // The normalized URL the most recent TEST actually fetched.
    private string? _lastTestedUrl;
    private bool _testing;

    public EditFeedWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _globalColor = settings.PanelColor;
        _removeArmTimer.Tick += (_, _) => DisarmRemove();
        SourceInitialized += (_, _) => MaxHeight = WorkArea().Height;
        Loaded += (_, _) => { KeepOnScreen(); UrlBox.Focus(); };
        SizeChanged += (_, _) => KeepOnScreen(); // test results grow/shrink the form
        BuildPresets();
        PreviewTime.Text = DateTime.Now.ToString("HH:mm") + " · just now";
        RefreshPreview();
    }

    public EditFeedWindow(AppSettings settings, FeedEntry existing) : this(settings)
    {
        HeaderText.Text = "EDIT FEED";
        Title = "Edit feed";
        RemoveButton.Visibility = Visibility.Visible;
        _originalUrl = existing.Url;
        UrlBox.Text = existing.Url;
        NameBox.Text = existing.Name;
        IntervalBox.Text = Math.Max(1, (int)Math.Round(existing.IntervalSeconds / 60.0)).ToString();
        IncludeBox.Text = existing.IncludeKeywords;
        ExcludeBox.Text = existing.ExcludeKeywords;
        ActiveCheck.IsChecked = existing.Enabled;
        SoundCheck.IsChecked = existing.PlaySound;
        ColorBox.Text = existing.Color ?? "";
    }

    // ===== Screen fit =====
    // SizeToContent + NoResize would otherwise let a tall form (high DPI,
    // 768px screens) push SAVE off the bottom of the screen. MaxHeight caps
    // the window; the form then scrolls above a pinned footer.

    /// <summary>Work area of the monitor the owner (or this dialog) is on, in DIPs.</summary>
    private Rect WorkArea()
    {
        var handle = new WindowInteropHelper(Owner ?? this).Handle;
        var px = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                         ?? System.Windows.Media.Matrix.Identity;
        return new Rect(fromDevice.Transform(new System.Windows.Point(px.Left, px.Top)),
                        fromDevice.Transform(new System.Windows.Point(px.Right, px.Bottom)));
    }

    private void KeepOnScreen()
    {
        if (!IsLoaded) return;
        var area = WorkArea();
        if (Top + ActualHeight > area.Bottom) Top = area.Bottom - ActualHeight;
        if (Top < area.Top) Top = area.Top;
    }

    // Hairline above the footer while the form is cut off, so it reads as scrollable.
    private void FormScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        => FooterBar.BorderThickness = new Thickness(0, FormScroller.ScrollableHeight > 0 ? 1 : 0, 0, 0);

    // ===== Colour + preview =====

    private void BuildPresets()
    {
        foreach (var (name, hex) in Presets)
        {
            var chip = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                Cursor = Cursors.Hand,
                ToolTip = $"{name} {hex}"
            };
            chip.MouseLeftButtonUp += (_, _) => ColorBox.Text = hex;
            PresetPanel.Children.Add(chip);
        }
    }

    private void Preview_Changed(object sender, TextChangedEventArgs e) => RefreshPreview();

    /// <summary>Blank colour previews the global one, so the preview always shows what the panel will look like.</summary>
    private void RefreshPreview()
    {
        if (PreviewTile == null || ColorSwatch == null) return; // during InitializeComponent
        var blank = ColorBox.Text.Trim().Length == 0;
        ColorPlaceholder.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        var color = ValidationHelpers.ParseColor(blank ? _globalColor : ColorBox.Text);
        ColorSwatch.Background = color is Color c ? new SolidColorBrush(c) : Brushes.Transparent;

        var accent = color ?? Color.FromRgb(0x5E, 0x71, 0x91);
        var brush = new SolidColorBrush(accent);
        PreviewTile.BorderBrush = brush;
        PreviewStripe.Fill = brush;
        PreviewFeed.Foreground = brush;
        PreviewTile.Effect = new DropShadowEffect { Color = accent, BlurRadius = 16, ShadowDepth = 0, Opacity = 0.35 };

        var name = NameBox.Text.Trim();
        PreviewFeed.Text = (name.Length > 0 ? name : _feedTitle ?? "FEED NAME").ToUpperInvariant();
    }

    private void ColorSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var seed = ColorBox.Text.Trim().Length > 0 ? ColorBox.Text : _globalColor;
        if (ValidationHelpers.PickColor(seed) is string hex)
            ColorBox.Text = hex;
    }

    // ===== Test =====

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton == null) return; // during InitializeComponent
        _saveAnywayUrl = null;
        SaveButton.Content = "SAVE";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await TestAsync();
        }
        catch (Exception ex)
        {
            App.LogError("Feed test", ex);
        }
    }

    /// <summary>Fetches the URL (following a web page's feed link); true when a feed came back.</summary>
    private async Task<bool> TestAsync()
    {
        HideError();
        var url = ValidationHelpers.NormalizeFeedUrl(UrlBox.Text);
        if (url == null)
        {
            ShowError("Enter a web address, e.g. https://feeds.bbci.co.uk/news/rss.xml");
            return false;
        }

        _testing = true;
        _lastTestedUrl = url;
        UrlBox.IsReadOnly = true; // the result must describe what's in the box
        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ShowTestResult("Checking…", TextDimBrush);
        try
        {
            var result = await FeedFetcher.FetchAsync(url, null, null, _closing.Token, discover: true);
            if (result.Outcome != FetchOutcome.Ok || result.Feed is not { } feed)
            {
                var code = result.Error ?? "FAIL";
                ShowTestResult($"✗ {code} — {Explain(code)}", OfflineBrush);
                return false;
            }

            var feedUrl = result.DiscoveredUrl ?? url;
            UrlBox.Text = feedUrl; // show the normalized form, or the feed found on the page
            _testedInput = UrlBox.Text;
            _testedFeedUrl = feedUrl;
            _feedTitle = feed.Title.Length > 0 ? feed.Title : null;
            if (NameBox.Text.Trim().Length == 0 && _feedTitle != null)
                NameBox.Text = _feedTitle;

            var newest = feed.Items.OrderByDescending(i => i.Published ?? DateTime.MinValue).FirstOrDefault();
            if (newest != null) PreviewTitle.Text = newest.Title;

            var found = result.DiscoveredUrl != null ? "found on the page  ·  " : "";
            ShowTestResult($"✓ {found}{feed.Format}  ·  {feed.Items.Count} {(feed.Items.Count == 1 ? "story" : "stories")}" +
                           (newest?.Published is DateTime p ? $"  ·  newest {p.ToLocalTime():d MMM HH:mm}" : ""),
                           OnlineBrush);
            RefreshPreview();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false; // dialog closed mid-test
        }
        finally
        {
            _testing = false;
            if (!_closing.IsCancellationRequested)
            {
                UrlBox.IsReadOnly = false;
                TestButton.IsEnabled = true;
                SaveButton.IsEnabled = true;
            }
        }
    }

    private static string Explain(string code) => code switch
    {
        "DNS" => "the server name doesn't exist (typo?).",
        "TIMEOUT" => $"no answer within {FeedFetcher.Timeout.TotalSeconds:0} seconds.",
        "REFUSED" or "CONN FAIL" or "UNREACH" => "couldn't connect to the server.",
        "CERT" => "the site's HTTPS certificate isn't valid.",
        "TLS" => "the secure connection failed.",
        "NOT A FEED" => "that address returned something that isn't RSS, Atom or JSON Feed.",
        "NO FEED ON PAGE" => "that's a web page, but it doesn't advertise a feed. Look for an RSS link on the site.",
        "BAD XML" => "the feed is too broken to read.",
        "BAD JSON" => "the JSON is too broken to read.",
        "TOO BIG" => "the response is over 8 MB.",
        "HTTP 401" or "HTTP 403" => "the server refused access.",
        "HTTP 404" or "HTTP 410" => "nothing at that address.",
        "HTTP 429" => "the server is rate-limiting requests; try a longer interval.",
        _ when code.StartsWith("HTTP 5") => "the server had an error; it may be temporary.",
        _ => "couldn't read a feed at that address."
    };

    private Brush TextDimBrush => (Brush)FindResource("TextDimBrush");
    private Brush OnlineBrush => (Brush)FindResource("OnlineBrush");
    private Brush OfflineBrush => (Brush)FindResource("OfflineBrush");

    private void ShowTestResult(string text, Brush brush)
    {
        TestResultText.Text = text;
        TestResultText.Foreground = brush;
        TestResultText.Visibility = Visibility.Visible;
    }

    // ===== Remove (two clicks, so a stray click can't delete a feed) =====

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_removeArmTimer.IsEnabled)
        {
            _removeArmTimer.Stop();
            RemoveRequested = true;
            DialogResult = true;
            return;
        }
        RemoveButton.Content = "CLICK AGAIN TO REMOVE";
        RemoveButton.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x3B, 0x5C));
        _removeArmTimer.Start();
    }

    private void DisarmRemove()
    {
        _removeArmTimer.Stop();
        RemoveButton.Content = "REMOVE";
        RemoveButton.ClearValue(BackgroundProperty);
    }

    // ===== Save =====

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveAsync();
        }
        catch (Exception ex)
        {
            App.LogError("Feed save", ex);
        }
    }

    private async Task SaveAsync()
    {
        if (_testing) return;
        HideError();

        var url = ValidationHelpers.NormalizeFeedUrl(UrlBox.Text);
        if (url == null)
        {
            ShowError("Enter a web address, e.g. https://feeds.bbci.co.uk/news/rss.xml");
            return;
        }

        if (!int.TryParse(IntervalBox.Text, out var minutes) || minutes < 1 || minutes > 1440)
        {
            ShowError("Check every must be between 1 and 1440 minutes.");
            return;
        }

        var color = ColorBox.Text.Trim();
        if (color.Length > 0 && ValidationHelpers.ParseColor(color) == null)
        {
            ShowError("Panel colour must be a valid hex value (e.g. #FFB020) or left blank for the default.");
            return;
        }

        // Check the feed before saving: catches typos and swaps a site's address
        // for its feed. Unchanged URLs on edit, and "save anyway", skip it.
        var unchanged = _originalUrl != null && url == _originalUrl;
        if (!unchanged && _saveAnywayUrl != url)
        {
            var ok = _testedInput == UrlBox.Text || await TestAsync();
            if (_closing.IsCancellationRequested || !IsLoaded) return; // closed while checking
            if (!ok)
            {
                _saveAnywayUrl = _lastTestedUrl;
                SaveButton.Content = "SAVE ANYWAY";
                ShowError("Couldn't read a feed at this address (see above). Save anyway to keep trying on schedule.");
                return;
            }
            if (_testedFeedUrl != null && _testedInput == UrlBox.Text) url = _testedFeedUrl;
        }

        var name = NameBox.Text.Trim();
        Result = new FeedEntry
        {
            Name = name.Length > 0 ? name : _feedTitle ?? new Uri(url).Host,
            Url = url,
            IntervalSeconds = minutes * 60,
            IncludeKeywords = TidyKeywords(IncludeBox.Text),
            ExcludeKeywords = TidyKeywords(ExcludeBox.Text),
            Enabled = ActiveCheck.IsChecked == true,
            PlaySound = SoundCheck.IsChecked == true,
            Color = color.Length > 0 ? color : null
        };
        DialogResult = true;
    }

    /// <summary>"a,, b ,c " -> "a, b, c".</summary>
    private static string TidyKeywords(string text)
        => string.Join(", ", text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(EventArgs e)
    {
        _removeArmTimer.Stop();
        _closing.Cancel();
        base.OnClosed(e);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
