using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Microsoft.Win32;

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
    private const double SidebarWidth = 240;
    private const double SidebarOpenDuration = 280;
    private const double SidebarCloseDuration = 230;

    private readonly Workspace _workspace;
    private Stopwatch? _sidebarAnimationClock;
    private double _sidebarAnimationFromWidth;
    private double _sidebarAnimationToWidth;
    private double _sidebarAnimationFromOpacity;
    private double _sidebarAnimationToOpacity;
    private bool _sidebarAnimationClosing;
    // Parallax translation applied to the sidebar inner content while the
    // column smoothly expands/collapses for a fluid, polished reveal.
    private readonly TranslateTransform _sidebarContentTranslate = new();
    private bool _draggingPane;
    private LeafPane? _dragPane;
    private System.Windows.Point _dragStart;
    private DropZone _dropZone;
    // Tab-bar reorder drag state: armed on any tab mouse-down, becomes an
    // active drag after a few pixels of travel (so clicks/renames still work).
    private WorkspaceTab? _dragTab;
    private bool _dragTabActive;
    private System.Windows.Point _dragTabStart;
    private bool _markerVisible;
    // Per-tab animated horizontal offsets (TranslateTransform) that push tabs
    // aside during a reorder drag; keyed by container instance so offsets stay
    // per-visual even as the collection reorders.
    private readonly Dictionary<DependencyObject, TranslateTransform> _tabOffsets = new();
    private int _tabSlotIndex = -1;
    private bool _ghostVisible;
    private double _ghostWidth;

    public MainWindow()
    {
        _workspace = SessionStore.Load();
        InitializeComponent();
        DataContext = _workspace;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        PreviewMouseMove += MainWindow_PreviewMouseMove;
        PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
        StateChanged += MainWindow_StateChanged;
        UpdateLayoutForWindowState();

        // Keep the file search rooted at the selected project.
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedProject))
                FileSearch.SetRoot(_workspace.SelectedProject?.WorkingDirectory ?? "");
        };
        FileSearch.SetRoot(_workspace.SelectedProject?.WorkingDirectory ?? "");

        // Parallax translation on sidebar inner content
        SidebarContent.RenderTransform = _sidebarContentTranslate;

        // Restore the persisted sidebar state, collapsed (no animation) when
        // the user closed it last time.
        if (!AppSettings.Instance.SidebarVisible)
        {
            SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarPanel.Opacity = 0;
        }

        // Session restoration assigns the initial selection before bindings
        // create its terminal view. Focus it after the first frame so exactly
        // one shell starts, and the window is immediately ready for typing.
        ContentRendered += (_, _) =>
        {
            Model.StartupMark.Note("content rendered");
            Dispatcher.BeginInvoke(
                () => _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus(),
                DispatcherPriority.ContextIdle);
        };

        // Sidebar visibility is a single source of truth: any change (toolbar
        // button, keyboard, or the settings toggle) animates the panel.
        AppSettings.Instance.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>Creates the window with a pre-loaded workspace (from async
    /// startup). Skips the synchronous SessionStore.Load() call that the
    /// parameterless constructor performs.</summary>
    public MainWindow(Workspace workspace)
    {
        _workspace = workspace;
        SessionStore.Current = workspace;
        InitializeComponent();
        DataContext = _workspace;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        PreviewMouseMove += MainWindow_PreviewMouseMove;
        PreviewMouseLeftButtonUp += MainWindow_PreviewMouseLeftButtonUp;
        StateChanged += MainWindow_StateChanged;
        UpdateLayoutForWindowState();

        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedProject))
                FileSearch.SetRoot(_workspace.SelectedProject?.WorkingDirectory ?? "");
        };
        FileSearch.SetRoot(_workspace.SelectedProject?.WorkingDirectory ?? "");

        SidebarContent.RenderTransform = _sidebarContentTranslate;

        if (!AppSettings.Instance.SidebarVisible)
        {
            SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarPanel.Opacity = 0;
        }

        ContentRendered += (_, _) =>
        {
            Model.StartupMark.Note("content rendered");
            Dispatcher.BeginInvoke(
                () => _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus(),
                DispatcherPriority.ContextIdle);
        };

        AppSettings.Instance.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.ThemeName) or nameof(AppSettings.Appearance))
        {
            // The acrylic tint is painted by DWM, outside the XAML palette, so
            // it must be re-applied when the theme (and thus the chrome tint)
            // changes. The wash density differs per appearance too.
            ApplyBackdrop();
            return;
        }

        if (e.PropertyName != nameof(AppSettings.SidebarVisible))
            return;

        AnimateSidebar(
            AppSettings.Instance.SidebarVisible ? SidebarWidth : 0,
            fadeOut: !AppSettings.Instance.SidebarVisible);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
        // Some Windows 10 builds ignore an accent applied before the first
        // frame is shown; re-apply once the window has actually rendered.
        ContentRendered += (_, _) => ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        // Dark chrome keeps the DWM blur-behind with a theme-tinted wash. The
        // light chrome is fully opaque instead: translucent bright surfaces
        // over a blurred desktop read muddy, and DWM skips recomposition on a
        // tint change until the next move/resize — which showed up as a
        // washed-out window after switching appearance until it was dragged.
        if (AppSettings.Instance.IsDarkAppearance)
        {
            if (WindowBackdrop.EnableAcrylic(this,
                    ChromePalette.BackdropTint(AppSettings.Instance.ThemeName), alpha: 0x30))
                Background = Brushes.Transparent;
        }
        else
        {
            WindowBackdrop.Disable(this);
            Background = (Brush)FindResource("VexBackground");
        }
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
                MaximizeButtonIcon.Text = "\uE923";
                MaximizeButton.ToolTip = "Restore";
                MainGrid.Margin = new Thickness(6);
            }
            else
            {
                MaximizeButtonIcon.Text = "\uE922";
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
        SystemCommands.CloseWindow(this);
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;

        // Plain Escape closes the settings overlay while it is open, before the
        // terminal (or any focused control inside it) sees the key. Chorded
        // Escape (e.g. Ctrl+Escape) still belongs to the terminal.
        if (key == System.Windows.Input.Key.Escape && modifiers == System.Windows.Input.ModifierKeys.None
            && _settingsOverlay is { Visibility: Visibility.Visible })
        {
            _settingsOverlay.Hide();
            e.Handled = true;
            return;
        }

        // A full-screen terminal application owns its entire keyspace. In
        // particular, applications such as vim, htop, and lazygit commonly
        // use Ctrl+Shift chords that otherwise look like Vex shortcuts.
        if (System.Windows.Input.Keyboard.FocusedElement is NativeTerminalControl { IsTuiMode: true })
            return;

        // Plain Ctrl+<letter> is never claimed here, so a full-screen TUI's own
        // bindings (opencode's Ctrl+P, vim-style apps, ...) always reach the PTY.
        // Vex owns Ctrl+Shift chords instead; Ctrl+Shift+P opens the palette,
        // which also hosts Settings.
        if (modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) && key == System.Windows.Input.Key.P)
        {
            ShowCommandPalette();
            e.Handled = true;
        }
        else if (modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) && key == System.Windows.Input.Key.M)
        {
            ToggleThemeSwitcher();
            e.Handled = true;
        }
        else if (modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) && key == System.Windows.Input.Key.Space)
        {
            ToggleTabPeek();
            e.Handled = true;
        }
        else if (modifiers == System.Windows.Input.ModifierKeys.Control && key == System.Windows.Input.Key.S)
        {
            if (SaveCurrentFile())
            {
                e.Handled = true;
            }
        }
    }

    private CommandPalette? _paletteOverlay;
    private CommandPalette PaletteOverlay
    {
        get
        {
            if (_paletteOverlay is null)
            {
                _paletteOverlay = new CommandPalette();
                _paletteOverlay.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = MainGrid });
                _paletteOverlay.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = MainGrid });
                PalettePopup.Child = _paletteOverlay;
            }
            return _paletteOverlay;
        }
    }

    private SettingsOverlay? _settingsOverlay;
    private SettingsOverlay SettingsOverlay
    {
        get
        {
            if (_settingsOverlay is null)
            {
                _settingsOverlay = new SettingsOverlay();
                _settingsOverlay.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = MainGrid });
                _settingsOverlay.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = MainGrid });
                _settingsOverlay.Hidden += () =>
                    _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus();
                SettingsPopup.Child = _settingsOverlay;
            }
            return _settingsOverlay;
        }
    }

    private ThemeSwitcher? _themeSwitcher;
    private ThemeSwitcher ThemeSwitcher
    {
        get
        {
            if (_themeSwitcher is null)
            {
                _themeSwitcher = new ThemeSwitcher();
                _themeSwitcher.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = MainGrid });
                _themeSwitcher.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = MainGrid });
                ThemePopup.Child = _themeSwitcher;
            }
            return _themeSwitcher;
        }
    }

    private TabPeek? _tabPeek;
    private TabPeek TabPeek
    {
        get
        {
            if (_tabPeek is null)
            {
                _tabPeek = new TabPeek();
                _tabPeek.SetBinding(WidthProperty, new Binding("ActualWidth") { Source = MainGrid });
                _tabPeek.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = MainGrid });
                TabPeekPopup.Child = _tabPeek;
            }
            return _tabPeek;
        }
    }

    private bool SaveCurrentFile()
    {
        if (_workspace.SelectedProject?.SelectedTab?.ActiveLeaf is EditorPane editorPane)
        {
            editorPane.Save();
            return true;
        }
        return false;
    }

    private void ShowCommandPalette()
    {
        var items = PaletteProvider.GetItems(_workspace, action => 
        {
            if (action == "NewProject") NewProject_Click(this, new RoutedEventArgs());
            else if (action == "Settings") Settings_Click(this, new RoutedEventArgs());
            else if (action == "ThemePicker") ToggleThemeSwitcher();
        });
        PaletteOverlay.Show(items);
    }

    private void ToggleThemeSwitcher()
    {
        if (_themeSwitcher is { Visibility: Visibility.Visible })
            _themeSwitcher.Hide();
        else
            ThemeSwitcher.Show();
    }

    private void ToggleTabPeek()
    {
        if (_tabPeek is { Visibility: Visibility.Visible })
            _tabPeek.Hide();
        else if (_workspace.SelectedProject is { } project)
            TabPeek.Show(project);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a project directory" };
        if (dialog.ShowDialog(this) == true)
            _workspace.NewProject(dialog.FolderName);
    }

    private void RefreshFileTree_Click(object sender, RoutedEventArgs e)
    {
        _workspace.SelectedProject?.RefreshFileTree();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Toggle();
    }

    private void FileSearch_FileOpenRequested(string filePath)
    {
        _workspace.SelectedProject?.OpenFile(filePath);
    }

    private void HideSidebar_Click(object sender, RoutedEventArgs e)
        => AppSettings.Instance.SidebarVisible = false;

    private void ShowSidebar_Click(object sender, RoutedEventArgs e)
        => AppSettings.Instance.SidebarVisible = true;

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
        => AppSettings.Instance.SidebarVisible = !AppSettings.Instance.SidebarVisible;

    private void AnimateSidebar(double target, bool fadeOut = false)
    {
        // Freeze VT/conpty resizes until the animation settles; otherwise
        // every frame reflows the terminal buffer (see ResizeSuspended).
        NativeTerminalControl.ResizeSuspended = true;
        _sidebarAnimationClosing = fadeOut;
        _sidebarAnimationFromWidth = SidebarColumn.ActualWidth;
        _sidebarAnimationToWidth = target;
        _sidebarAnimationFromOpacity = SidebarPanel.Visibility == Visibility.Visible ? SidebarPanel.Opacity : 0;
        _sidebarAnimationToOpacity = fadeOut ? 0 : 1;

        if (!fadeOut)
        {
            SidebarPanel.Visibility = Visibility.Visible;
            if (_sidebarAnimationFromWidth <= 0 && SidebarPanel.Opacity <= 0)
                _sidebarAnimationFromOpacity = 0;
        }

        _sidebarAnimationClock = Stopwatch.StartNew();
        CompositionTarget.Rendering -= SidebarAnimation_Rendering;
        CompositionTarget.Rendering += SidebarAnimation_Rendering;
    }

    private void SidebarAnimation_Rendering(object? sender, EventArgs e)
    {
        var duration = _sidebarAnimationClosing ? SidebarCloseDuration : SidebarOpenDuration;
        var elapsed = _sidebarAnimationClock?.Elapsed.TotalMilliseconds ?? duration;
        var t = Math.Clamp(elapsed / duration, 0.0, 1.0);

        // Fluid easing: Quartic Ease-Out on reveal for natural deceleration;
        // smoothstep on collapse for soft exit.
        double eased;
        if (!_sidebarAnimationClosing)
        {
            // Deceleration curve: instant response on click, softly gliding into dock
            eased = 1.0 - Math.Pow(1.0 - t, 3.2);
        }
        else
        {
            // Smooth acceleration and deceleration for clean collapse
            eased = t * t * (3.0 - 2.0 * t);
        }

        // Animate column width smoothly
        var width = _sidebarAnimationFromWidth + ((_sidebarAnimationToWidth - _sidebarAnimationFromWidth) * eased);
        SidebarColumn.Width = new GridLength(Math.Max(0, width), GridUnitType.Pixel);

        if (_sidebarAnimationClosing)
        {
            SidebarPanel.Opacity = Math.Clamp(_sidebarAnimationFromOpacity + (_sidebarAnimationToOpacity - _sidebarAnimationFromOpacity) * eased, 0, 1);
            _sidebarContentTranslate.X = -18.0 * eased;
        }
        else
        {
            var targetOpacity = Math.Clamp(eased * 1.25, 0, 1);
            SidebarPanel.Opacity = targetOpacity;
            _sidebarContentTranslate.X = -18.0 * (1.0 - eased);
        }

        if (t < 1.0)
            return;

        CompositionTarget.Rendering -= SidebarAnimation_Rendering;
        _sidebarAnimationClock = null;
        NativeTerminalControl.ResizeSuspended = false;

        if (_sidebarAnimationClosing)
        {
            SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarPanel.Opacity = 0;
            _sidebarContentTranslate.X = 0;
        }
        else
        {
            SidebarColumn.Width = new GridLength(SidebarWidth, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarPanel.Opacity = 1;
            _sidebarContentTranslate.X = 0;
        }
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
            leaf.Close();
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

    private void TabHeader_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTab tab } && _workspace.SelectedProject is { } project)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Middle)
            {
                project.CloseTab(tab);
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
                _dragTabStart = e.GetPosition(this);
            }
        }
    }

    private void PaneTitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only start a drag from the title bar itself, not from its buttons.
        if (FindAncestorButton(e.OriginalSource as DependencyObject) is not null)
            return;
        if (sender is FrameworkElement { DataContext: LeafPane leaf })
        {
            if (_workspace.SelectedProject?.SelectedTab is { } tab)
                tab.ActiveLeaf = leaf;

            _dragPane = leaf;
            _dragStart = e.GetPosition(this);
            _draggingPane = false;
            _dropZone = DropZone.None;
            // Capture now so the move/up are delivered even when the cursor
            // leaves the title bar during the drag.
            System.Windows.Input.Mouse.Capture(this);
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;

        // A click on a non-interactive surface (sidebar padding, pane chrome,
        // empty tree space) leaves keyboard focus stranded in a text box like
        // the file search, so typing goes nowhere. Let the click settle, and
        // if nothing took focus, return it to the active pane.
        if (System.Windows.Input.Keyboard.FocusedElement is not TextBox focused)
            return;
        if (IsVisualDescendantOf(focused, e.OriginalSource as DependencyObject))
            return; // clicking the box itself repositions the caret

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, focused))
                _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus();
        });
    }

    private static bool IsVisualDescendantOf(DependencyObject ancestor, DependencyObject? node)
    {
        for (; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
                return true;
        }
        return false;
    }

    private static Button? FindAncestorButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button button)
                return button;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void MainWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HandleTabDragMove(e);

        if (_dragPane is null)
            return;

        if (!_draggingPane)
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStart.X) + Math.Abs(pos.Y - _dragStart.Y) < 8)
                return;
            _draggingPane = true;
            DropOverlay.Visibility = Visibility.Visible;
            HideSplitPreview();
        }

        var point = e.GetPosition(ContentArea);
        var (target, bounds) = PaneUnder(point);
        var zone = target is not null && ReferenceEquals(target, _dragPane)
            ? ZoneInBounds(point, bounds)
            : DropZone.None;
        _dropZone = zone;
        if (zone != DropZone.None)
            ShowSplitPreview(PreviewRect(bounds, zone));
        else
            HideSplitPreview();
        e.Handled = true;
    }

    private void MainWindow_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HandleTabDragDrop();

        var pane = _dragPane;
        var zone = _dropZone;
        var wasDragging = _draggingPane;
        _draggingPane = false;
        _dragPane = null;
        _dropZone = DropZone.None;

        // Release the drag capture taken on mouse-down (even for a plain click).
        if (System.Windows.Input.Mouse.Captured == this)
            System.Windows.Input.Mouse.Capture(null);

        if (!wasDragging)
            return;

        DropOverlay.Visibility = Visibility.Collapsed;
        HideSplitPreview();

        if (pane is not null && zone != DropZone.None)
        {
            pane.Split(zone == DropZone.Left || zone == DropZone.Right
                ? Orientation.Horizontal
                : Orientation.Vertical);
        }
        e.Handled = true;
    }

    private (LeafPane? Pane, Rect Bounds) PaneUnder(System.Windows.Point point)
    {
        var hosts = new List<(LeafPane Pane, FrameworkElement Element)>();
        CollectPaneHosts(ContentArea, hosts);
        foreach (var (pane, element) in hosts)
        {
            var bounds = element.TransformToAncestor(ContentArea)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Contains(point))
                return (pane, bounds);
        }
        // Fallback: if the visual walk found nothing, treat the dragged pane as
        // filling the whole content area so the drop still works.
        if (hosts.Count == 0 && _dragPane is not null)
            return (_dragPane, new Rect(0, 0, ContentArea.ActualWidth, ContentArea.ActualHeight));
        return (null, default);
    }

    /// <summary>Handles tab-bar drags: grows into a visible drag once past the
    /// threshold, and moves the insertion marker with the pointer.</summary>
    private void HandleTabDragMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTab is null)
            return;

        // The tab may have been closed out from under the drag (its terminal
        // exited); drop the stale drag silently.
        if (_workspace.SelectedProject is not { } project || !project.Tabs.Contains(_dragTab))
        {
            CancelTabDrag();
            return;
        }

        if (!_dragTabActive)
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragTabStart.X) + Math.Abs(pos.Y - _dragTabStart.Y) < 6)
                return;
            _dragTabActive = true;
            System.Windows.Input.Mouse.Capture(this);
            ShowTabGhost();
        }

        UpdateTabDropMarker(e.GetPosition(TabDragCanvas));
        UpdateTabGhostPosition(e.GetPosition(TabDragCanvas));
        e.Handled = true;
    }

    /// <summary>Shows the drag ghost (a copy of the dragged tab's header)
    /// following the pointer, and hides the tab's original slot.</summary>
    private void ShowTabGhost()
    {
        if (_dragTab is null)
            return;
        // Capture the real width BEFORE hiding the slot (a collapsed element
        // measures 0).
        var slot = TabStrip.ItemContainerGenerator.ContainerFromItem(_dragTab) as FrameworkElement;
        var width = slot is null ? 120 : slot.ActualWidth;
        _ghostWidth = Math.Max(100, width);
        TabDragGhostHost.Width = _ghostWidth;
        TabDragGhost.Content = _dragTab;
        TabDragGhostHost.Height = Math.Min(30, TabBarGrid.ActualHeight - 6);
        _ghostVisible = true;
        Canvas.SetLeft(TabDragGhostHost, _dragTabStart.X - _ghostWidth / 2);
        Canvas.SetTop(TabDragGhostHost, (TabBarGrid.ActualHeight - TabDragGhostHost.Height) / 2.0);
        TabDragGhostHost.Visibility = Visibility.Visible;
        TabDragGhostHost.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        // Hide the original slot: it is now represented by the ghost.
        HideTabSlot(_dragTab);
    }

    /// <summary>Hides a tab's ListBoxItem container (the dragged tab's slot)
    /// so the strip shows a gap where it was picked up. Fades and shrinks it
    /// so the tab reads as "lifting off" rather than vanishing.</summary>
    private void HideTabSlot(WorkspaceTab tab)
    {
        var item = TabStrip.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement;
        if (item is null)
            return;
        var transform = GetTabOffset(item);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        item.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)));
        // Collapse only after the fade so the gap opens smoothly.
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            // The drag may have ended and the slot restored meanwhile; only
            // collapse if this tab is still the one being dragged.
            if (ReferenceEquals(_dragTab, tab))
                item.Visibility = Visibility.Collapsed;
        };
        t.Start();
    }

    private TranslateTransform GetTabOffset(FrameworkElement item)
    {
        if (_tabOffsets.TryGetValue(item, out var transform))
            return transform;
        transform = new TranslateTransform();
        item.RenderTransform = transform;
        _tabOffsets[item] = transform;
        return transform;
    }

    /// <summary>Animated push-away: tabs after the drop slot slide right so the
    /// strip always previews the final layout.</summary>
    private void AnimateTabPush(Dictionary<int, double> targetOffsets)
    {
        foreach (var item in TabStrip.Items)
        {
            var container = TabStrip.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container is null || ReferenceEquals(item, _dragTab))
                continue;
            var idx = TabStrip.Items.IndexOf(item);
            var target = targetOffsets.TryGetValue(idx, out var v) ? v : 0.0;
            var transform = GetTabOffset(container);
            if (Math.Abs(transform.X - target) < 0.5)
                continue;
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(transform.X, target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    /// <summary>Computes the push offset (by collection index) that previews the
    /// final layout: every tab at or after the drop slot shifts right by the
    /// ghost's width, the rest stay put.</summary>
    private Dictionary<int, double> PushOffsets(WorkspaceTab dragged, int slot)
    {
        var offsets = new Dictionary<int, double>();
        var project = _workspace.SelectedProject!;
        var draggedIndex = project.Tabs.IndexOf(dragged);
        var visible = 0;
        for (var i = 0; i < project.Tabs.Count; i++)
        {
            if (i == draggedIndex)
                continue;
            if (visible >= slot)
                offsets[i] = _ghostWidth;
            visible++;
        }
        return offsets;
    }

    private void UpdateTabGhostPosition(System.Windows.Point pt)
    {
        if (!_ghostVisible || _dragTab is null)
            return;
        Canvas.SetLeft(TabDragGhostHost, pt.X - _ghostWidth / 2);
    }

    /// <summary>Snaps the insertion marker to the gap between tabs.</summary>
    private void UpdateTabDropMarker(System.Windows.Point pt)
    {
        if (_dragTab is null || _workspace.SelectedProject is not { } project)
            return;

        // A lone tab has nowhere to go, and leaving the strip vertically
        // hides the marker until the pointer comes back.
        var slot = project.Tabs.Count > 1 && pt.Y >= -8 && pt.Y <= TabBarGrid.ActualHeight + 8
            ? TabInsertIndexAt(pt, project.Tabs, _dragTab)
            : null;

        // Keep tabs pushed aside so the gap matches the ghost position.
        if (slot is { } s)
        {
            if (_tabSlotIndex != s)
            {
                _tabSlotIndex = s;
                AnimateTabPush(PushOffsets(_dragTab, s));
            }
        }
        else if (_tabSlotIndex != -1)
        {
            _tabSlotIndex = -1;
            AnimateTabPush(new Dictionary<int, double>());
        }

        if (!_markerVisible && slot is not null)
        {
            TabDropMarker.BeginAnimation(UIElement.OpacityProperty, null);
            TabDropMarker.Opacity = 0;
            TabDropMarker.Visibility = Visibility.Visible;
            _markerVisible = true;
        }

        if (slot is { } s2)
        {
            var left = VisibleSlotX(project.Tabs, _dragTab, s2) - 1.0;
            Canvas.SetTop(TabDropMarker, (TabBarGrid.ActualHeight - TabDropMarker.Height) / 2.0);
            TabDropMarker.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
            Canvas.SetLeft(TabDropMarker, left);
            TabDropMarker.Width = 2.0;
        }
        else if (_markerVisible)
        {
            TabDropMarker.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(90)));
        }
    }

    /// <summary>Drop slot under the pointer: the number of visible tabs (all
    /// but the dragged one) whose right edge is left of the pointer. Null when
    /// the pointer sits over the dragged tab's own home slot, where dropping
    /// is a no-op.</summary>
    private static int? TabInsertIndexAt(System.Windows.Point pt, System.Collections.ObjectModel.ObservableCollection<WorkspaceTab> tabs, WorkspaceTab dragged)
    {
        var draggedIndex = tabs.IndexOf(dragged);
        var listBox = ((MainWindow)Application.Current.MainWindow).TabStrip;
        var width = 0.0;
        var slot = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i == draggedIndex)
                continue;
            width += (listBox.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)?.ActualWidth ?? 0;
            if (pt.X <= width)
                return slot == draggedIndex ? null : slot;
            slot++;
        }
        return slot == draggedIndex ? null : slot;
    }

    /// <summary>X of the gap after <paramref name="slot"/> visible tabs (all
    /// but the dragged one), i.e. where the drop marker sits.</summary>
    private static double VisibleSlotX(System.Collections.ObjectModel.ObservableCollection<WorkspaceTab> tabs, WorkspaceTab dragged, int slot)
    {
        var draggedIndex = tabs.IndexOf(dragged);
        var listBox = ((MainWindow)Application.Current.MainWindow).TabStrip;
        var x = 0.0;
        var visible = 0;
        for (var i = 0; i < tabs.Count && visible < slot; i++)
        {
            if (i == draggedIndex)
                continue;
            x += (listBox.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)?.ActualWidth ?? 0;
            visible++;
        }
        return x;
    }

    /// <summary>Drops a dragged tab. The strip already previews the final
    /// layout, so the collection move plus a transform reset (layout and
    /// offset cancel out exactly) puts the tab in place with no jump.</summary>
    private void HandleTabDragDrop()
    {
        var dragged = _dragTab;
        var wasActive = _dragTabActive;
        var slot = _tabSlotIndex;
        var project = _workspace.SelectedProject;
        _dragTab = null;
        _dragTabActive = false;
        _tabSlotIndex = -1;
        HideTabGhost();
        HideTabMarker();
        ReleaseTabDragCapture();

        if (!wasActive || dragged is null || slot < 0 || project is null)
        {
            ResetTabOffsets();
            return;
        }

        project.MoveTab(dragged, slot);
        // After the reorder each tab's layout position is exactly what the
        // push previewed, so clearing the offsets is invisible.
        ResetTabOffsets();
        TabStrip.UpdateLayout();
    }

    /// <summary>Restores the hidden dragged-tab slot and clears all push
    /// offsets.</summary>
    private void ResetTabOffsets()
    {
        foreach (var (item, transform) in _tabOffsets)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
            if (item is FrameworkElement fe)
            {
                fe.BeginAnimation(UIElement.OpacityProperty, null);
                fe.Opacity = 1;
                if (fe.Visibility == Visibility.Collapsed)
                    fe.Visibility = Visibility.Visible;
            }
        }
        _tabOffsets.Clear();
    }

    /// <summary>Clears all tab-drag state: hidden marker and ghost, restored
    /// offsets, released capture. Used when the dragged tab is closed
    /// mid-drag.</summary>
    private void CancelTabDrag()
    {
        _dragTab = null;
        _dragTabActive = false;
        _tabSlotIndex = -1;
        HideTabGhost();
        HideTabMarker();
        ResetTabOffsets();
        ReleaseTabDragCapture();
    }

    private void HideTabGhost()
    {
        if (!_ghostVisible)
            return;
        TabDragGhostHost.BeginAnimation(UIElement.OpacityProperty, null);
        TabDragGhostHost.Visibility = Visibility.Collapsed;
        TabDragGhostHost.Opacity = 0;
        _ghostVisible = false;
    }

    private void HideTabMarker()
    {
        if (!_markerVisible)
            return;
        TabDropMarker.BeginAnimation(UIElement.OpacityProperty, null);
        TabDropMarker.Visibility = Visibility.Collapsed;
        TabDropMarker.Opacity = 0;
        _markerVisible = false;
    }

    private void ReleaseTabDragCapture()
    {
        if (System.Windows.Input.Mouse.Captured == this)
            System.Windows.Input.Mouse.Capture(null);
    }

    private static void CollectPaneHosts(DependencyObject node, List<(LeafPane Pane, FrameworkElement Element)> result)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is FrameworkElement fe && fe.DataContext is LeafPane pane)
            {
                result.Add((pane, fe));
                continue; // a pane never nests another pane; skip its content
            }
            CollectPaneHosts(child, result);
        }
    }

    private static DropZone ZoneInBounds(System.Windows.Point p, Rect bounds)
    {
        var x = (p.X - bounds.X) / bounds.Width;
        var y = (p.Y - bounds.Y) / bounds.Height;
        const double edge = 0.25;
        if (x < edge && y >= edge && y <= 1 - edge) return DropZone.Left;
        if (x > 1 - edge && y >= edge && y <= 1 - edge) return DropZone.Right;
        if (y < edge && x >= edge && x <= 1 - edge) return DropZone.Top;
        if (y > 1 - edge && x >= edge && x <= 1 - edge) return DropZone.Bottom;
        return DropZone.None;
    }

    private static Rect PreviewRect(Rect bounds, DropZone zone)
    {
        const double fraction = 0.42;
        const double inset = 4;
        return zone switch
        {
            DropZone.Left => new Rect(bounds.X + inset, bounds.Y + inset, bounds.Width * fraction - inset, bounds.Height - inset * 2),
            DropZone.Right => new Rect(bounds.X + bounds.Width * (1 - fraction), bounds.Y + inset, bounds.Width * fraction - inset, bounds.Height - inset * 2),
            DropZone.Top => new Rect(bounds.X + inset, bounds.Y + inset, bounds.Width - inset * 2, bounds.Height * fraction - inset),
            DropZone.Bottom => new Rect(bounds.X + inset, bounds.Y + bounds.Height * (1 - fraction), bounds.Width - inset * 2, bounds.Height * fraction - inset),
            _ => Rect.Empty,
        };
    }

    private void ShowSplitPreview(Rect rect)
    {
        Canvas.SetLeft(SplitPreview, rect.X);
        Canvas.SetTop(SplitPreview, rect.Y);
        SplitPreview.Width = rect.Width;
        SplitPreview.Height = rect.Height;
        if (SplitPreview.Visibility == Visibility.Visible)
            return;
        SplitPreview.Visibility = Visibility.Visible;
        SplitPreview.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void HideSplitPreview()
    {
        if (SplitPreview.Visibility == Visibility.Collapsed)
            return;
        SplitPreview.BeginAnimation(OpacityProperty, null);
        SplitPreview.Opacity = 0;
        SplitPreview.Visibility = Visibility.Collapsed;
    }

    private void TitleEditBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            CommitTabRename(sender);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CancelTabRename(sender);
            e.Handled = true;
        }
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitTabRename(sender);
    }

    private void CommitTabRename(object sender)
    {
        if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is WorkspaceTab tab)
        {
            var panel = (Grid)textBox.Parent;
            var textBlock = (TextBlock)panel.FindName("TitleBlock");
            if (textBlock != null)
            {
                var newTitle = textBox.Text.Trim();
                if (!string.IsNullOrEmpty(newTitle) && newTitle != tab.Title)
                {
                    tab.Title = newTitle;
                    tab.HasCustomTitle = true;
                }
                
                textBox.Visibility = Visibility.Collapsed;
                textBlock.Visibility = Visibility.Visible;
            }
        }
    }

    private void CancelTabRename(object sender)
    {
        if (sender is System.Windows.Controls.TextBox textBox && textBox.DataContext is WorkspaceTab tab)
        {
            var panel = (Grid)textBox.Parent;
            var textBlock = (TextBlock)panel.FindName("TitleBlock");
            if (textBlock != null)
            {
                textBox.Text = tab.Title; // revert
                textBox.Visibility = Visibility.Collapsed;
                textBlock.Visibility = Visibility.Visible;
            }
        }
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceTab tab } && _workspace.SelectedProject is { } project)
            project.CloseTab(tab);
        e.Handled = true;
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

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= SidebarAnimation_Rendering;
        NativeTerminalControl.ResizeSuspended = false;
        AppSettings.Instance.Flush(); // persist the debounced settings write
        SessionStore.Save(_workspace); // persist projects, tabs and divider positions
        base.OnClosed(e);
        foreach (var project in _workspace.Projects)
            foreach (var tab in project.Tabs)
                DisposePane(tab.Root);
    }

    private static void DisposePane(PaneNode node)
    {
        switch (node)
        {
            case LeafPane leaf:
                leaf.Dispose();
                break;
            case SplitPane split:
                DisposePane(split.First);
                DisposePane(split.Second);
                break;
        }
    }
}
