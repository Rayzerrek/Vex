using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Media;
using Vex.App.Model;
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

public partial class MainWindow : Window
{
    private const double SidebarWidth = 240;
    private const double SidebarAnimDuration = 300;

    private readonly Workspace _workspace;
    private Stopwatch? _sidebarAnimationClock;
    private double _sidebarAnimationFrom;
    private double _sidebarAnimationTo;
    private bool _sidebarAnimationFadingOut;
    private bool _draggingPane;
    private LeafPane? _dragPane;
    private System.Windows.Point _dragStart;
    private DropZone _dropZone;

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

        // Restore the persisted sidebar state, collapsed (no animation) when
        // the user closed it last time.
        if (!AppSettings.Instance.SidebarVisible)
        {
            SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarPanel.Opacity = 0;
        }

        // Sidebar visibility is a single source of truth: any change (toolbar
        // button, keyboard, or the settings toggle) animates the panel.
        AppSettings.Instance.PropertyChanged += OnSettingsPropertyChanged;

        // When the settings overlay finishes closing, hand keyboard focus back
        // to the active pane so typing is never stranded after Escape/close.
        SettingsOverlay.Hidden += () =>
            _workspace.SelectedProject?.SelectedTab?.ActiveLeaf?.Focus();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ThemeName))
        {
            // The acrylic tint is painted by DWM, outside the XAML palette, so
            // it must be re-applied when the theme (and thus the chrome tint)
            // changes.
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
        // Acrylic/blur-behind with a theme-tinted graphite replaces the
        // painted gradient when DWM accepts the backdrop; the XAML gradient
        // stays as the fallback, since a transparent window without a
        // backdrop is black.
        if (WindowBackdrop.EnableAcrylic(this, ChromePalette.BackdropTint(AppSettings.Instance.ThemeName), alpha: 0x30))
            Background = Brushes.Transparent;
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
            && SettingsOverlay.Visibility == Visibility.Visible)
        {
            SettingsOverlay.Hide();
            e.Handled = true;
            return;
        }

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
        else if (modifiers == System.Windows.Input.ModifierKeys.Control && key == System.Windows.Input.Key.S)
        {
            if (SaveCurrentFile())
            {
                e.Handled = true;
            }
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
        if (ThemeSwitcher.Visibility == Visibility.Visible)
            ThemeSwitcher.Hide();
        else
            ThemeSwitcher.Show();
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
        _sidebarAnimationFrom = SidebarColumn.ActualWidth;
        _sidebarAnimationTo = target;
        _sidebarAnimationFadingOut = fadeOut;

        // Prepare the side the animation is moving to so there is no layout
        // jump on the very first frame.
        if (fadeOut)
        {
            SidebarColumn.Width = new GridLength(SidebarWidth, GridUnitType.Pixel);
        }
        else
        {
            SidebarPanel.Visibility = Visibility.Visible;
        }

        _sidebarAnimationClock = Stopwatch.StartNew();
        CompositionTarget.Rendering -= SidebarAnimation_Rendering;
        CompositionTarget.Rendering += SidebarAnimation_Rendering;
    }

    private void SidebarAnimation_Rendering(object? sender, EventArgs e)
    {
        // Smoothstep easing: starts and ends gently instead of lurching.
        var elapsed = _sidebarAnimationClock?.Elapsed.TotalMilliseconds ?? SidebarAnimDuration;
        var t = Math.Min(elapsed / SidebarAnimDuration, 1);
        var eased = t * t * (3 - 2 * t);

        // Width eases smoothly; the reveal button is eased over the same
        // timeline (fade + slight scale) rather than popping at the end.
        var width = _sidebarAnimationFrom + ((_sidebarAnimationTo - _sidebarAnimationFrom) * eased);
        SidebarColumn.Width = new GridLength(width, GridUnitType.Pixel);

        if (_sidebarAnimationFadingOut)
        {
            SidebarPanel.Opacity = 1 - eased;
        }
        else
        {
            // Fade the content in only once the panel is mostly on screen so
            // it never flashes over the terminal.
            var contentOpacity = Math.Clamp((eased - 0.35) / 0.5, 0, 1);
            SidebarPanel.Opacity = contentOpacity;
        }

        if (eased < 1)
            return;

        CompositionTarget.Rendering -= SidebarAnimation_Rendering;
        SidebarColumn.Width = new GridLength(_sidebarAnimationTo, GridUnitType.Pixel);
        SidebarPanel.Opacity = 1;
        _sidebarAnimationClock = null;

        if (_sidebarAnimationFadingOut)
        {
            SidebarPanel.Visibility = Visibility.Collapsed;
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
        }
    }

    private void PaneTitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Only start a drag from the title bar itself, not from its buttons.
        if (FindAncestorButton(e.OriginalSource as DependencyObject) is not null)
            return;
        if (sender is FrameworkElement { DataContext: LeafPane leaf })
        {
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

    private void MainWindow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
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
