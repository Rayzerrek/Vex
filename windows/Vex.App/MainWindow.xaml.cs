using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Media;
using Vex.App.Model;
using Microsoft.Win32;

namespace Vex.App;

public partial class MainWindow : Window
{
    private const double SidebarWidth = 240;
    private const double SidebarAnimDuration = 300;

    private readonly Workspace _workspace;
    private bool _sidebarAnimationInProgress;
    private Stopwatch? _sidebarAnimationClock;
    private double _sidebarAnimationFrom;
    private double _sidebarAnimationTo;
    private bool _sidebarAnimationFadingOut;

    public MainWindow()
    {
        _workspace = SessionStore.Load();
        InitializeComponent();
        DataContext = _workspace;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        StateChanged += MainWindow_StateChanged;
        UpdateLayoutForWindowState();

        // Restore the persisted sidebar state, collapsed (no animation) when
        // the user closed it last time.
        if (!AppSettings.Instance.SidebarVisible)
        {
            SidebarColumn.Width = new GridLength(0, GridUnitType.Pixel);
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarPanel.Opacity = 0;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Acrylic blur with a deep blue tint replaces the painted gradient
        // when DWM accepts the backdrop; the XAML gradient stays as the
        // fallback, since a transparent window without a backdrop is black.
        if (WindowBackdrop.EnableAcrylic(this, Color.FromRgb(0x0A, 0x14, 0x20), alpha: 0x30))
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

    // Dragging the empty tab-bar strip moves the window, mirroring how the
    // caption area behaves — the ListBox swallows clicks, so this region
    // provides the remaining grab space.
    private bool _tabBarDragging;

    private void TabBarDrag_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _tabBarDragging = true;
    }

    private void TabBarDrag_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _tabBarDragging = false;
    }

    private void TabBarDrag_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_tabBarDragging && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            _tabBarDragging = false;
            DragMove();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;

        if (modifiers == System.Windows.Input.ModifierKeys.Control && key == System.Windows.Input.Key.P)
        {
            ShowCommandPalette();
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
        });
        PaletteOverlay.Show(items);
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a project directory" };
        if (dialog.ShowDialog(this) == true)
            _workspace.NewProject(dialog.FolderName);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Toggle();
    }

    private void HideSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarAnimationInProgress)
            return;

        AppSettings.Instance.SidebarVisible = false;
        AnimateSidebar(0, fadeOut: true);
    }

    private void ShowSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_sidebarAnimationInProgress)
            return;

        AppSettings.Instance.SidebarVisible = true;
        AnimateSidebar(SidebarWidth, fadeOut: false);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Instance.SidebarVisible)
            HideSidebar_Click(sender, e);
        else
            ShowSidebar_Click(sender, e);
    }

    private void AnimateSidebar(double target, bool fadeOut = false)
    {
        _sidebarAnimationInProgress = true;
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
        _sidebarAnimationInProgress = false;

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
