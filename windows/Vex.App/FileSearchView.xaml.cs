using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class FileSearchView : UserControl
{
    private FileSearchEngine? _engine;
    private string _root = "";
    private List<FileSearchResult> _allResults = new();
    private bool _rebuildQueued;
    private bool _indexDirty = true;
    private CancellationTokenSource? _searchCts;
    private HalfDebouncer<string>? _searchDebouncer;

    public FileSearchView()
    {
        InitializeComponent();
    }

    /// <summary>Fired when the user activates a result (Enter or double-click).</summary>
    public event Action<string>? FileOpenRequested;

    public void SetRoot(string workingDirectory)
    {
        _root = workingDirectory;
        _engine = new FileSearchEngine(workingDirectory);
        _indexDirty = true;
        _searchDebouncer?.Cancel();
        _searchCts?.Cancel();
        _searchCts = null;
        if (!string.IsNullOrEmpty(SearchBox.Text))
            SearchBox.Text = "";
        else
            ScheduleSearch();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // The index walk is deferred until the user actually starts searching,
        // so startup never spends time enumerating the project directory.
        if (_indexDirty)
            RebuildIndex();
    }

    private void RebuildIndex() => _ = RebuildIndexAsync();

    private async Task RebuildIndexAsync()
    {
        if (_engine == null || _rebuildQueued)
            return;

        _rebuildQueued = true;
        _indexDirty = false;
        // Snapshot the root: the index may be rebuilding when the project
        // switches, and we must not overwrite a newer root's index.
        var root = _root;
        var engine = _engine;
        try
        {
            await Task.Run(() => engine.RebuildIndex());
        }
        finally
        {
            _rebuildQueued = false;
        }

        if (engine == _engine && root == _root)
            ScheduleSearch();
    }

    private void ScheduleSearch()
    {
        var query = SearchBox.Text;
        SearchHint.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

        if (_engine == null || string.IsNullOrWhiteSpace(query))
        {
            _searchDebouncer?.Cancel();
            _searchCts?.Cancel();
            _searchCts = null;
            ApplySearchResults(new List<FileSearchResult>());
            return;
        }

        // Half-debounce: the initial keystroke after quiet executes immediately (0ms latency),
        // rapid subsequent keystrokes within typing burst (120ms) coalesce to trailing edge off UI thread.
        _searchDebouncer ??= new HalfDebouncer<string>(TimeSpan.FromMilliseconds(120), QueueSearch, leadingEdge: true);
        _searchDebouncer.Trigger(query);
    }

    private void QueueSearch(string query)
    {
        // The leading edge runs inline on the UI thread; the trailing edge
        // comes from HalfDebouncer's timer. Marshal only that trailing call so
        // CTS replacement and view state stay single-threaded without adding
        // latency to the first keystroke.
        if (Dispatcher.CheckAccess())
        {
            PerformSearch(query);
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (query == SearchBox.Text)
                PerformSearch(query);
        });
    }

    private void PerformSearch(string query)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var engine = _engine;
        if (engine == null)
            return;

        _ = Task.Run(() =>
        {
            if (cts.IsCancellationRequested)
                return;

            var results = engine.Search(query);

            if (cts.IsCancellationRequested)
                return;

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (cts.IsCancellationRequested)
                    return;

                ApplySearchResults(results);
            });
        }, cts.Token);
    }

    private void ApplySearchResults(List<FileSearchResult> results)
    {
        _allResults = results;
        ResultList.ItemsSource = results;
        ResultList.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (results.Count > 0)
            ResultList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ScheduleSearch();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ResultList.SelectedIndex < ResultList.Items.Count - 1)
                {
                    ResultList.SelectedIndex++;
                    ResultList.ScrollIntoView(ResultList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (ResultList.SelectedIndex > 0)
                {
                    ResultList.SelectedIndex--;
                    ResultList.ScrollIntoView(ResultList.SelectedItem);
                }
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (ResultList.SelectedItem is FileSearchResult result)
            FileOpenRequested?.Invoke(result.FullPath);
    }
}
