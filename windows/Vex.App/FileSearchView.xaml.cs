using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

public partial class FileSearchView : UserControl
{
    private readonly DispatcherTimer _debounce;
    private FileSearchEngine? _engine;
    private string _root = "";
    private List<FileSearchResult> _allResults = new();
    private bool _rebuildQueued;
    private bool _indexDirty = true;

    public FileSearchView()
    {
        InitializeComponent();

        // Typing runs the query immediately; rebuilding the on-disk index is
        // deferred and only happens once the user stops typing for a moment.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => RebuildIndex();
    }

    /// <summary>Fired when the user activates a result (Enter or double-click).</summary>
    public event Action<string>? FileOpenRequested;

    public void SetRoot(string workingDirectory)
    {
        _root = workingDirectory;
        _engine = new FileSearchEngine(workingDirectory);
        _indexDirty = true;
        SearchBox.Text = "";
        UpdateResults();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // The index walk is deferred until the user actually starts searching,
        // so startup never spends time enumerating the project directory.
        if (_indexDirty)
            RebuildIndex();
    }

    private void RebuildIndex()
    {
        _debounce.Stop();
        if (_engine == null || _rebuildQueued)
            return;

        _rebuildQueued = true;
        _indexDirty = false;
        // Snapshot the root: the index may be rebuilding when the project
        // switches, and we must not overwrite a newer root's index.
        var root = _root;
        var engine = _engine;
        Task.Run(() => engine.RebuildIndex()).ContinueWith(_ =>
        {
            _rebuildQueued = false;
            if (engine == _engine && root == _root)
                UpdateResults();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateResults()
    {
        var query = SearchBox.Text;
        SearchHint.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;

        if (_engine == null || string.IsNullOrWhiteSpace(query))
        {
            _allResults = new List<FileSearchResult>();
            ResultList.ItemsSource = null;
            ResultList.Visibility = Visibility.Collapsed;
            return;
        }

        _allResults = _engine.Search(query);
        ResultList.ItemsSource = _allResults;
        ResultList.Visibility = _allResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_allResults.Count > 0)
            ResultList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateResults();
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
