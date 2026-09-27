using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using NewsWatch.Services;

namespace NewsWatch;

/// <summary>Live view of every new story since launch; one instance, owned by the main window.</summary>
public partial class HistoryWindow : Window
{
    private readonly StoryLog _log;
    private readonly ICollectionView _view;

    public HistoryWindow(StoryLog log)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _log = log;
        _view = CollectionViewSource.GetDefaultView(log.Events);
        _view.Filter = Matches;
        StoryList.ItemsSource = _view;
        StoryList.SelectionChanged += (_, _) =>
            OpenButton.IsEnabled = (StoryList.SelectedItem as StoryEvent)?.HasLink == true;
        log.Events.CollectionChanged += OnEventsChanged;
        log.StatusChanged += OnStatusChanged;
        Closed += (_, _) =>
        {
            log.Events.CollectionChanged -= OnEventsChanged;
            log.StatusChanged -= OnStatusChanged;
            _view.Filter = null; // the default view is shared: don't leave our filter on it
        };
        Refresh();
    }

    private void OnEventsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnStatusChanged(object? sender, EventArgs e) => Refresh();

    private bool Matches(object o)
    {
        var term = SearchBox.Text.Trim();
        return term.Length == 0 || o is StoryEvent s &&
               (s.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.Feed.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                s.Summary.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        Refresh();
    }

    private void Refresh()
    {
        var count = _log.Events.Count;
        var panels = _log.Events.Count(x => x.Shown == "PANEL");
        var shown = _view.Cast<object>().Count();
        SummaryText.Text = $"Since the app started at {_log.Started:HH:mm, d MMM yyyy}  ·  " +
                           (count == 0 ? "no new stories" : $"{count} new {(count == 1 ? "story" : "stories")}, {panels} as panels") +
                           (shown != count ? $"  ·  {shown} matching" : "");
        EmptyText.Text = count == 0 ? "No new stories since the app started." : "Nothing matches the search.";
        EmptyText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = count > 0;
    }

    private void StoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: StoryEvent story })
            ValidationHelpers.OpenInBrowser(story.Link);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
        => ValidationHelpers.OpenInBrowser((StoryList.SelectedItem as StoryEvent)?.Link);

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export history",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            FileName = $"NewsWatch-history-{DateTime.Now:yyyy-MM-dd-HHmm}.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            // UTF-8 with BOM so Excel reads non-ASCII headlines correctly.
            File.WriteAllText(dialog.FileName, _log.ToCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(this, $"Couldn't save the file:\n{ex.Message}", "NEWS//WATCH",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
