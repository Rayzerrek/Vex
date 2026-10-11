using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class TabPeek : OverlayControl
{
    private Project? _project;
    private readonly DispatcherTimer _previewTimer;
    private List<TabPeekPreview> _previews = new();
    private int _nextPreview;

    internal bool IsRefreshingPreviews => _previewTimer.IsEnabled;

    public TabPeek()
    {
        InitializeComponent();
        HideOnWindowDeactivate();
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _previewTimer.Tick += (_, _) => RefreshVisiblePreviews();
        Unloaded += (_, _) => ReleasePreviews();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _previewTimer.Stop(); };
    }

    protected override void HideCore() => Hide();

    /// <summary>Raised after Tab Peek closed, so the window can hand keyboard
    /// focus back to the terminal.</summary>
    public event Action? Hidden;

    public void Show(Project project)
    {
        ReleasePreviews();
        _project = project;
        project.Tabs.CollectionChanged += OnTabsChanged;
        RebuildPreviews();

        // Pre-select the active tab so the user starts nearby.
        var activeIndex = project.SelectedTab is { } selected
            ? project.Tabs.IndexOf(selected) : -1;
        TabList.SelectedIndex = activeIndex >= 0 ? activeIndex : 0;

        Visibility = Visibility.Visible;
        OpenPopup(this);
        AnimateOverlayOpen(Backdrop, Panel, PanelScale, PanelTranslate);

        _previewTimer.Start();
        Dispatcher.BeginInvoke(() =>
        {
            if (Visibility != Visibility.Visible || !_previewTimer.IsEnabled) return;
            TabList.Focus();
            RefreshVisiblePreviews();
        }, DispatcherPriority.Input);
    }

    public void Hide()
    {
        _previewTimer.Stop();
        HideWithAnimation(Backdrop, Panel, () =>
        {
            ReleasePreviews();
            Hidden?.Invoke();
        });
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildPreviews();

    private void RebuildPreviews()
    {
        var selected = (TabList.SelectedItem as TabPeekPreview)?.Tab ?? _project?.SelectedTab;
        foreach (var preview in _previews) preview.Dispose();
        _previews = _project?.Tabs.Select(tab => new TabPeekPreview(tab)).ToList() ?? new();
        TabList.ItemsSource = _previews;
        TabList.SelectedItem = _previews.FirstOrDefault(preview => ReferenceEquals(preview.Tab, selected));
        _nextPreview = 0;
    }

    private void ReleasePreviews()
    {
        _previewTimer.Stop();
        if (_project is not null) _project.Tabs.CollectionChanged -= OnTabsChanged;
        _project = null;
        foreach (var preview in _previews) preview.Dispose();
        _previews.Clear();
        TabList.ItemsSource = null;
    }

    private void RefreshVisiblePreviews()
    {
        if (!IsVisible || _previews.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        // Offscreen thumbnails get no rendering work; a round-robin budget
        // prevents a large project from holding up input while opening Peek.
        for (var visited = 0; visited < _previews.Count; visited++)
        {
            var preview = _previews[_nextPreview];
            _nextPreview = (_nextPreview + 1) % _previews.Count;
            if (TabList.ItemContainerGenerator.ContainerFromItem(preview) is ListBoxItem { IsVisible: true } item
                && item.TransformToAncestor(TabList).TransformBounds(new Rect(item.RenderSize)).IntersectsWith(new Rect(TabList.RenderSize)))
                preview.RefreshTerminalPreviews();
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 6) break;
        }
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => Hide();

    private void Panel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void TabList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TabList, e.OriginalSource as DependencyObject)
            is ListBoxItem item)
        {
            TabList.SelectedItem = item.DataContext;
            ActivateSelected();
            Hide();
            e.Handled = true;
        }
    }

    private void TabList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                ActivateSelected();
                Hide();
                e.Handled = true;
                break;
            case Key.Left:
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = TabList.Items.Count;
        if (count == 0)
            return;
        var next = ((TabList.SelectedIndex + delta) % count + count) % count;
        TabList.SelectedIndex = next;
    }

    private void TabList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Navigation highlights a preview without activating or resizing its terminal.
        if (TabList.SelectedItem is { } selected) TabList.ScrollIntoView(selected);
    }

    private void ActivateSelected()
    {
        if (_project is null || TabList.SelectedItem is not TabPeekPreview preview)
            return;
        _project.SelectedTab = preview.Tab;
        preview.Tab.FocusAttentionPane();
    }
}
