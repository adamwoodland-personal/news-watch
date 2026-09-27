using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NewsWatch.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace NewsWatch;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _settings = settings;

        CloseToTrayCheck.IsChecked = settings.CloseToTray;
        ConfirmExitCheck.IsChecked = settings.ConfirmOnExit;
        AutoStartCheck.IsChecked = StartupService.IsEnabled();
        SideCombo.SelectedIndex = settings.PanelSide == PanelSide.Right ? 1 : 0;
        DurationBox.Text = settings.PanelDurationSeconds.ToString();
        MaxOnScreenBox.Text = settings.MaxPanelsOnScreen.ToString();
        PerCheckBox.Text = settings.MaxPanelsPerCheck.ToString();
        CatchUpBox.Text = settings.CatchUpPerFeed.ToString();
        MaxAgeBox.Text = settings.MaxStoryAgeHours.ToString();
        SummaryCheck.IsChecked = settings.ShowSummary;
        ImagesCheck.IsChecked = settings.ShowImages;
        ColorBox.Text = settings.PanelColor;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"NEWS//WATCH v{version?.ToString(3) ?? "1.0.0"} — news feed monitor";
    }

    private void ColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ColorSwatch == null) return; // during InitializeComponent
        var color = ValidationHelpers.ParseColor(ColorBox.Text);
        ColorSwatch.Background = color is Color c ? new SolidColorBrush(c) : Brushes.Transparent;
    }

    private void ColorSwatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ValidationHelpers.PickColor(ColorBox.Text) is string hex)
            ColorBox.Text = hex;
    }

    private void ResetColor_Click(object sender, RoutedEventArgs e) => ColorBox.Text = AppSettings.DefaultPanelColor;

    private static bool TryRange(NumberBox box, int min, int max, out int value)
        => int.TryParse(box.Text, out value) && value >= min && value <= max;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRange(DurationBox, 1, 3600, out var secs)) { ShowError("Panel time must be between 1 and 3600 seconds."); return; }
        if (!TryRange(MaxOnScreenBox, 1, 20, out var onScreen)) { ShowError("Panels on screen must be between 1 and 20."); return; }
        if (!TryRange(PerCheckBox, 1, 20, out var perCheck)) { ShowError("Panels per check must be between 1 and 20."); return; }
        if (!TryRange(CatchUpBox, 0, 20, out var catchUp)) { ShowError("Catch-up must be between 0 and 20."); return; }
        if (!TryRange(MaxAgeBox, 0, 720, out var maxAge)) { ShowError("Story age limit must be between 0 and 720 hours."); return; }
        if (ValidationHelpers.ParseColor(ColorBox.Text) == null)
        {
            ShowError("Colour must be a valid hex value, e.g. #FFB020.");
            return;
        }

        // When enabling, always rewrite the entry: repairs a stale path if the
        // exe has moved since auto-start was first ticked.
        var wantAutoStart = AutoStartCheck.IsChecked == true;
        var autoStartOk = wantAutoStart
            ? StartupService.SetEnabled(true)
            : !StartupService.IsEnabled() || StartupService.SetEnabled(false);
        if (!autoStartOk)
        {
            System.Windows.MessageBox.Show(this,
                "Couldn't update the auto-start entry in the registry. Other settings were still saved.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _settings.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _settings.ConfirmOnExit = ConfirmExitCheck.IsChecked == true;
        _settings.PanelSide = SideCombo.SelectedIndex == 1 ? PanelSide.Right : PanelSide.Left;
        _settings.PanelDurationSeconds = secs;
        _settings.MaxPanelsOnScreen = onScreen;
        _settings.MaxPanelsPerCheck = perCheck;
        _settings.CatchUpPerFeed = catchUp;
        _settings.MaxStoryAgeHours = maxAge;
        _settings.ShowSummary = SummaryCheck.IsChecked == true;
        _settings.ShowImages = ImagesCheck.IsChecked == true;
        _settings.PanelColor = ColorBox.Text.Trim();
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
