using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private ScrollViewer? _tabScrollViewer;

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

    public MainWindow() : this(SessionStore.Load()) { }

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

        SidebarContent.RenderTransform = _sidebarContentTranslate;

        if (!AppSettings.Instance.SidebarVisible)
        {
            SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarPanel.Opacity = 0;
        }

        // Keep the file search rooted at the selected project.
        _workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workspace.SelectedProject))
                FileSearch.SetRoot(_workspace.SelectedProject?.WorkingDirectory ?? "");
        };
        FileSearch.SetRoot(workspace.SelectedProject?.WorkingDirectory ?? "");

        foreach (var project in workspace.Projects)
            HookProject(project);
        workspace.Projects.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null)
                return;
            foreach (Project project in e.NewItems)
                HookProject(project);
        };

        Model.StartupMark.Note("ctor wiring done");

        ContentRendered += (_, _) =>
        {
            Model.StartupMark.Note("content rendered");
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

    private void HookProject(Project project)
    {
        project.TabDeactivating += tab =>
        {
            if (ReferenceEquals(project, _workspace.SelectedProject))
                CaptureTabPreview(tab);
        };
    }

    private void CaptureTabPreview(WorkspaceTab tab)
    {
        if (!ContentArea.IsLoaded || ContentArea.ActualWidth <= 0 || ContentArea.ActualHeight <= 0)
            return;

        const int previewWidth = 360;
        const int previewHeight = 220;
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var brush = new VisualBrush(ContentArea)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, previewWidth, previewHeight));
        }

        var bitmap = new RenderTargetBitmap(previewWidth, previewHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        bitmap.Freeze();
        tab.Preview = bitmap;
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
        // Some DWM builds ignore an accent applied before the first frame;
        // re-apply once the window has actually rendered.
        ContentRendered += (_, _) => ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
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
        Background = (Brush)FindResource("VexBackground");
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
        else if (modifiers == System.Windows.Input.ModifierKeys.Control && key == System.Windows.Input.Key.Tab)
        {
            CycleTab(1);
            e.Handled = true;
        }
        else if (modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) && key == System.Windows.Input.Key.Tab)
        {
            CycleTab(-1);
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

    private void CycleTab(int direction)
    {
        if (_workspace.SelectedProject is { } project && project.Tabs.Count > 1 && project.SelectedTab is { } currentTab)
        {
            var index = project.Tabs.IndexOf(currentTab);
            if (index >= 0)
            {
                var nextIndex = (index + direction) % project.Tabs.Count;
                if (nextIndex < 0) nextIndex += project.Tabs.Count;
                project.SelectedTab = project.Tabs[nextIndex];
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
                _paletteOverlay = new CommandPalette
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_paletteOverlay, 0);
                Grid.SetRowSpan(_paletteOverlay, 2);
                Panel.SetZIndex(_paletteOverlay, 1000);
                _paletteOverlay.Hidden += FocusActivePane;
                MainGrid.Children.Add(_paletteOverlay);
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
                _settingsOverlay = new SettingsOverlay
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_settingsOverlay, 0);
                Grid.SetRowSpan(_settingsOverlay, 2);
                Panel.SetZIndex(_settingsOverlay, 1001);
                _settingsOverlay.Hidden += FocusActivePane;
                MainGrid.Children.Add(_settingsOverlay);
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
                _themeSwitcher = new ThemeSwitcher
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_themeSwitcher, 0);
                Grid.SetRowSpan(_themeSwitcher, 2);
                Panel.SetZIndex(_themeSwitcher, 1002);
                _themeSwitcher.Hidden += FocusActivePane;
                MainGrid.Children.Add(_themeSwitcher);
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
                _tabPeek = new TabPeek
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetRow(_tabPeek, 0);
                Grid.SetRowSpan(_tabPeek, 2);
                Panel.SetZIndex(_tabPeek, 1003);
                _tabPeek.Hidden += FocusActivePane;
                MainGrid.Children.Add(_tabPeek);
            }
            return _tabPeek;
        }
    }

    /// <summary>Hands keyboard focus back to the active pane after an overlay
    /// closes, so typing goes straight to the terminal instead of stranding
    /// on the window. Dispatched after the popup's own focus restoration, and
    /// skipped when another overlay opened on top (palette → settings/theme)
    /// so the new overlay keeps its focus.</summary>
    private void FocusActivePane()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if ((_paletteOverlay?.Visibility == Visibility.Visible) ||
                (_settingsOverlay?.Visibility == Visibility.Visible) ||
                (_themeSwitcher?.Visibility == Visibility.Visible) ||
                (_tabPeek?.Visibility == Visibility.Visible))
                return;
            _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus();
        }, DispatcherPriority.ContextIdle);
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
            else if (action == "TabPeek") ToggleTabPeek();
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
        {
            if (project.SelectedTab is { } selected)
                CaptureTabPreview(selected);
            TabPeek.Show(project);
        }
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
        NativeTerminalControl.SuspendResizes();
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
        // Resume with an explicit recalc: the final width equals the last
        // animated frame, so no SizeChanged fires after this point — without
        // the resume event the grid would stay at the pre-animation size
        // (a fullscreen TUI like nvim would then cover only part of the pane).
        NativeTerminalControl.ResumeResizes();

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
                _dragTabStart = e.GetPosition(TabDragCanvas);
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
        NativeTerminalControl.ResumeResizes();
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
