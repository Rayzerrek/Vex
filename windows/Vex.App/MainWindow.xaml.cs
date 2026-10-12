using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

internal enum DropZone
{
    None,
    Left,
    Right,
    Top,
    Bottom,
}

public sealed partial class MainWindow : Window
{

    private readonly Workspace _workspace;
    private bool _draggingPane;
    private LeafPane? _dragPane;
    private System.Windows.Point _dragStart;
    private DropZone _dropZone;
    private ScrollViewer? _tabScrollViewer;
    private long _projectPickerClosedTimestamp;

    /// <summary>The tab strip's scroll viewer, resolved once from the visual
    /// tree. Layout-space math for the drag marker needs its horizontal
    /// offset, and the ListBox template does not guarantee a part name.</summary>
    private ScrollViewer? TabScrollViewer
    {
        get
        {
            if (_tabScrollViewer is null)
                _tabScrollViewer = FindDescendant<ScrollViewer>(TabStrip);
            return _tabScrollViewer;
        }
    }

    private static T? FindDescendant<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    public MainWindow() : this(SessionStore.Load())
    {
        ContentRendered += async (_, _) =>
        {
            if (_workspace.Projects.Count == 0 && ShellRegistry.IsWsl(AppSettings.Instance.ShellId))
                await OpenWslHomeAsync();
        };
    }

    /// <summary>Creates the window with a pre-loaded workspace (from async
    /// startup). Skips the synchronous SessionStore.Load() call that the
    /// parameterless constructor performs.</summary>
    public MainWindow(Workspace workspace)
    {
        _workspace = workspace;
        SessionStore.Current = workspace;
        Model.StartupMark.Note("xaml parse begin");
        InitializeComponent();
        Model.StartupMark.Note("xaml parsed");
        DataContext = workspace;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        PreviewMouseMove += MainWindow_PreviewMouseMove;
        PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
        StateChanged += MainWindow_StateChanged;
        UpdateLayoutForWindowState();


        // Refresh git branch when project changes.
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedProject) && _workspace.SelectedProject is { } project)
            {
                project.RefreshGitBranch();
                Terminal.Native.TerminalSessionPrewarmer.StartPrewarm(project.WorkingDirectory, project.DefaultShellId);
            }
        };
        Activated += (_, _) => _workspace.SelectedProject?.RefreshGitBranch();

        Model.StartupMark.Note("ctor wiring done");

        ContentRendered += (_, _) =>
        {
            Model.StartupMark.Note("content rendered");
#if DEBUG || VEX_SELFTEST
            if (Environment.GetEnvironmentVariable("VEX_STARTUP_PROFILE") == "1")
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Application.Current.Shutdown());
                return;
            }
#endif
            Dispatcher.BeginInvoke(
                () => _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus(),
                DispatcherPriority.ContextIdle);
            PrewarmOverlaysAfterStartup();
        };

        AppSettings.Instance.PropertyChanged += OnSettingsPropertyChanged;
    }

    private async void PrewarmOverlaysAfterStartup()
    {
        // Keep first-input latency clear of the two largest optional XAML
        // trees. A user opening either overlay during the delay still creates
        // it immediately through its lazy property.
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!IsLoaded)
            return;
        _ = PaletteOverlay;

        await Task.Delay(600);
        if (IsLoaded)
            _ = SettingsOverlay;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ShellId))
        {
            _wslHomeQuery?.Cancel();
            if (ShellRegistry.IsWsl(AppSettings.Instance.ShellId)) _ = OpenWslHomeAsync();
            else if (_workspace.Projects.Count == 0)
                _workspace.NewProject(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppSettings.Instance.ShellId);
            return;
        }

        if (e.PropertyName is nameof(AppSettings.ThemeName) or nameof(AppSettings.Appearance))
        {
            // The acrylic tint is painted by DWM, outside the XAML palette, so
            // it must be re-applied when the theme (and thus the chrome tint)
            // changes. The wash density differs per appearance too.
            ApplyBackdrop();
            return;
        }

    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
        // Some DWM builds ignore an accent applied before the first frame;
        // re-apply once the window has actually rendered.
        ContentRendered += (_, _) => ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        WindowFrame.ApplyRoundedFrame(this, AppSettings.Instance.IsDarkAppearance);

        // Dark chrome keeps modern DWM acrylic where it is reliable. Windows
        // 10 and any DWM that rejects the request use a fully opaque window;
        // the legacy blur path can leave stale WPF glyph tiles after Alt+Tab.
        if (AppSettings.Instance.IsDarkAppearance &&
            WindowBackdrop.EnableAcrylic(this,
                ChromePalette.BackdropTint(AppSettings.Instance.ThemeName), alpha: 0x30))
        {
            Background = Brushes.Transparent;
            return;
        }

        WindowBackdrop.Disable(this);
        SetResourceReference(BackgroundProperty, "VexBackground");
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateLayoutForWindowState();
    }

    private void UpdateLayoutForWindowState()
    {
        if (MaximizeButtonIcon != null && MaximizeButton != null)
        {
            if (WindowState == WindowState.Maximized)
            {
                MaximizeButtonIcon.Data = Geometry.Parse("M 5,4 V 3 Q 5,1 7,1 H 11 Q 13,1 13,3 V 7 Q 13,9 11,9 H 10 M 3,5 H 8 Q 10,5 10,7 V 11 Q 10,13 8,13 H 3 Q 1,13 1,11 V 7 Q 1,5 3,5 Z");
                MaximizeButton.ToolTip = "Restore";
                MainGrid.Margin = new Thickness(6);
            }
            else
            {
                MaximizeButtonIcon.Data = Geometry.Parse("M 4,2 H 10 Q 12,2 12,4 V 10 Q 12,12 10,12 H 4 Q 2,12 2,10 V 4 Q 2,2 4,2 Z");
                MaximizeButton.ToolTip = "Maximize";
                MainGrid.Margin = new Thickness(0);
            }
        }
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e)
    {
        _workspace.SelectedProject?.NewTab();
    }

    private void TabSplitRight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTab tab })
            tab.Split(System.Windows.Controls.Orientation.Horizontal);
    }

    private void PaneSplitRight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LeafPane leaf })
            leaf.Split(System.Windows.Controls.Orientation.Horizontal);
    }

    private void PaneSplitDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LeafPane leaf })
            leaf.Split(System.Windows.Controls.Orientation.Vertical);
    }

    private void PaneClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LeafPane leaf })
        {
            var info = TabCloseConfirmation.GetCloseInfo(leaf);
            if (info.NeedsConfirmation)
            {
                TabCloseOverlay.Show(info, () => leaf.Close());
            }
            else
            {
                leaf.Close();
            }
        }
    }

    private void PaneFocus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LeafPane leaf } ||
            _workspace.SelectedProject?.SelectedTab is not { } tab)
            return;

        tab.ActiveLeaf = leaf;
        tab.ToggleFocusMode();
    }

    private void TabSplitDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTab tab })
            tab.Split(System.Windows.Controls.Orientation.Vertical);
    }

    /// <summary>Wheel over the tab strip scrolls it horizontally. A vertical
    /// wheel is the only gesture a mouse has, and the strip has no visible
    /// scrollbar, so without this an overflowing strip would be unreachable.</summary>
    private void TabStrip_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (TabScrollViewer is not { } scroll || scroll.ScrollableWidth <= 0)
            return;
        scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Keeps the selected tab visible: keyboard tab switching and
    /// session restore both select a tab that may sit off the strip.</summary>
    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is not WorkspaceTab selected ||
            _workspace.SelectedProject is not { } project ||
            !project.Tabs.Contains(selected))
            return;

        // Selection is intentionally written to the model only for a live
        // item. During removal ListBox briefly reports null or the removed
        // item; a TwoWay binding used to push that transient state into the
        // content binding and leave the disposed tab visible.
        project.SelectedTab = selected;
        if (TabStrip.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement container)
            container.BringIntoView();
    }

    private void TabHeader_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTab tab } && _workspace.SelectedProject is { } project)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Middle)
            {
                RequestCloseTab(tab, sender as FrameworkElement);
                e.Handled = true;
                return;
            }

            project.SelectedTab = tab;

            if (e.ClickCount == 2 && e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                var textBlock = (TextBlock)((Grid)sender).FindName("TitleBlock");
                var textBox = (TextBox)((Grid)sender).FindName("TitleEditBox");
                if (textBlock != null && textBox != null)
                {
                    textBlock.Visibility = Visibility.Collapsed;
                    textBox.Visibility = Visibility.Visible;
                    textBox.Focus();
                    textBox.SelectAll();
                }
            }

            // Arm a reorder drag: it starts once the pointer travels a few
            // pixels, so plain clicks and double-click rename still work.
            // Skip when the press came from the close button or the rename
            // box; their own interactions (click / text selection) win.
            var source = e.OriginalSource as DependencyObject;
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left
                && FindAncestorButton(source) is null
                && FindAncestor<TextBox>(source) is null)
            {
                _dragTab = tab;
                _dragTabStart = e.GetPosition(TabDragCanvas);
            }
        }
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        // Works for both the hover button (DataContext = Project) and the context menu
        // (sender is MenuItem whose DataContext is also the Project via the DataTemplate).
        var project = (sender as FrameworkElement)?.DataContext as Vex.App.Model.Project;
        if (project is null) return;

        if (_workspace.Projects.Count <= 1)
        {
            MessageBox.Show(this, "Cannot remove the last project.", "Vex",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (project.Tabs.Count > 1)
        {
            var result = MessageBox.Show(this,
                $"Remove \"{project.Name}\" and close all its tabs?",
                "Remove project",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.OK) return;
        }

        _workspace.CloseProject(project);
        e.Handled = true;
    }

    private bool _forceClose;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }

        if (!AppSettings.Instance.ConfirmOnExit)
        {
            base.OnClosing(e);
            return;
        }

        var exitInfo = TabCloseConfirmation.GetWorkspaceExitInfo(_workspace);
        if (!exitInfo.NeedsConfirmation)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        TabCloseOverlay.ShowExit(exitInfo, () =>
        {
            _forceClose = true;
            Close();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _wslHomeQuery?.Cancel();
        _wslHomeQuery?.Dispose();
        _wslHomeQuery = null;
        AppSettings.Instance.PropertyChanged -= OnSettingsPropertyChanged;
        AppSettings.Instance.Flush(); // persist the debounced settings write
        SessionStore.Save(_workspace); // persist projects, tabs and divider positions
        base.OnClosed(e);
        foreach (var project in _workspace.Projects)
            project.Dispose();
    }
}

public enum WorkspaceShortcut
{
    CommandPalette,
    ThemeSwitcher,
    TabPeek,
    NewTab,
    ClosePaneOrTab,
    SplitRight,
    SplitDown,
    NextTab,
    PreviousTab,
    NextProject,
    PreviousProject,
    ProjectPicker,
    SwitchProject,
}
