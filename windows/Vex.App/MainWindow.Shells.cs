using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _wslHomeQuery;

    internal async Task OpenWslHomeAsync()
    {
        _wslHomeQuery?.Cancel();
        _wslHomeQuery?.Dispose();
        var query = new CancellationTokenSource();
        _wslHomeQuery = query;
        WorkspaceStatus.Text = "Opening WSL Home…";
        var selectedAtStart = _workspace.SelectedProject;
        try
        {
            var directory = await WslProjectLauncher.ResolveDefaultHomeAsync(query.Token);
            if (query.IsCancellationRequested || !IsLoaded || !ShellRegistry.IsWsl(AppSettings.Instance.ShellId)) return;
            if (!ReferenceEquals(_workspace.SelectedProject, selectedAtStart))
            {
                WorkspaceStatus.Text = "Choose a project to get started.";
                return;
            }
            _workspace.OpenWslHome(directory);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or FormatException)
        {
            if (!query.IsCancellationRequested && IsLoaded)
            {
                WorkspaceStatus.Text = "Could not open WSL Home. Choose a project to continue.";
                ShowWslProjectPicker(error.Message);
            }
        }
    }

    private sealed record ShellMenuChoice(string Name, string Environment, ImageSource? Icon, bool IsDefault);

    private static ShellProfile[] WindowsShells(string defaultShellId) => ShellChoiceList.Windows(
        ShellRegistry.Detected().Concat(AppSettings.Instance.CustomShells).Where(profile => !ShellRegistry.IsWsl(profile.Id)
            && File.Exists(profile.IsBuiltIn ? profile.Program : ShellRegistry.Resolve(profile.Id)?.Program)),
        defaultShellId, Vex.Terminal.TerminalSession.DefaultShell());

    private MenuItem ShellMenuItem(string title, Action? action = null)
    {
        var item = new MenuItem { Header = title };
        item.SetResourceReference(StyleProperty, "DarkMenuItem");
        if (action is not null) item.Click += (_, _) => action();
        return item;
    }

    private MenuItem CreateShellChoice(ShellProfile profile, Project project, Action<string> action)
    {
        var name = ShellChoiceList.Name(profile);
        var environment = ShellRegistry.IsWsl(profile.Id)
            ? WslShellPaths.ParseNetworkPath(project.WorkingDirectory)?.Distribution ?? "Linux"
            : project.IsWsl ? "Windows Home" : "Windows";
        var executable = Path.GetFileNameWithoutExtension(ShellChoiceList.Executable(profile));
        var icon = AppIconCatalog.ResolveIcon(executable) ?? AppIconCatalog.ResolveIcon(ShellRegistry.IsWsl(profile.Id) ? "wsl" : "cmd");
        var item = ShellMenuItem(name, () => action(profile.Id));
        item.Header = new ShellMenuChoice(name, environment, icon?.Image, profile.Id == project.DefaultShellId);
        item.Tag = profile.Id;
        item.ToolTip = profile.Program;
        item.SetResourceReference(StyleProperty, "ShellChoiceMenuItem");
        AutomationProperties.SetName(item, name);
        AutomationProperties.SetHelpText(item, environment + (profile.Id == project.DefaultShellId ? " · Project default" : ""));
        return item;
    }

    private IEnumerable<MenuItem> CreateShellChoices(IReadOnlyList<ShellProfile> available, Project project, Action<string> action)
    {
        var choices = ShellChoiceList.ForMenu(available, project.DefaultShellId);
        foreach (var profile in choices.Primary) yield return CreateShellChoice(profile, project, action);
        if (choices.More.Count == 0) yield break;
        var more = ShellMenuItem("More shells");
        foreach (var profile in choices.More) more.Items.Add(CreateShellChoice(profile, project, action));
        yield return more;
    }

    private async void PopulateShellChoices(ItemsControl menu, Project project, Action<string> action, bool defaultOnly = false)
    {
        menu.Items.Clear();
        var title = menu is MenuItem { Header: string submenuTitle } ? submenuTitle.Replace(" with", "")
            : defaultOnly ? "Project shell" : "New terminal";
        var heading = ShellMenuItem(title.ToUpperInvariant());
        heading.SetResourceReference(StyleProperty, "ShellChoiceHeading");
        heading.InputGestureText = "…";
        heading.ToolTip = defaultOnly ? "Applies to new terminals only. Existing terminals stay unchanged." : null;
        heading.IsEnabled = false;
        menu.Items.Add(heading);
        if (!project.IsWsl)
        {
            var available = WindowsShells(project.DefaultShellId);
            heading.InputGestureText = available.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var item in CreateShellChoices(available, project, action)) menu.Items.Add(item);
            return;
        }
        var loading = ShellMenuItem("Checking installed shells…");
        loading.IsEnabled = false;
        menu.Items.Add(loading);
        if (!defaultOnly)
        {
            var separator = new Separator();
            separator.SetResourceReference(StyleProperty, "ShellChoiceSeparator");
            menu.Items.Add(separator);
            var windows = ShellMenuItem("Windows shells");
            foreach (var item in CreateShellChoices(WindowsShells(project.DefaultShellId), project, action)) windows.Items.Add(item);
            windows.ToolTip = "Opens a terminal in your Windows Home folder.";
            menu.Items.Add(windows);
        }
        using var cancellation = new CancellationTokenSource();
        void Cancel(object? sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, menu)) cancellation.Cancel();
        }
        if (menu is ContextMenu context) context.Closed += Cancel;
        else if (menu is MenuItem submenu) submenu.SubmenuClosed += Cancel;
        try
        {
            var distribution = WslShellPaths.ParseNetworkPath(project.WorkingDirectory)?.Distribution;
            var profiles = await WslProjectLauncher.ListShellsAsync(distribution, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsLoaded || project.IsDisposed) return;
            menu.Items.Remove(loading);
            var position = 1;
            var available = ShellChoiceList.Linux(profiles, project.DefaultShellId);
            heading.InputGestureText = available.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var item in CreateShellChoices(available, project, action)) menu.Items.Insert(position++, item);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or FormatException)
        {
            if (!cancellation.IsCancellationRequested)
            {
                heading.InputGestureText = "—";
                loading.Header = "Could not load installed shells";
                loading.ToolTip = error.Message;
            }
        }
        finally
        {
            if (menu is ContextMenu contextMenu) contextMenu.Closed -= Cancel;
            else if (menu is MenuItem submenuItem) submenuItem.SubmenuClosed -= Cancel;
        }
    }

    private MenuItem ShellSubmenu(string title, Project project, Action<string> action)
    {
        var item = ShellMenuItem(title);
        item.Items.Add(ShellMenuItem("Loading…"));
        item.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, item)) PopulateShellChoices(item, project, action);
        };
        return item;
    }

    private void SetProjectShell(Project project, string shellId)
    {
        if (project.IsDisposed) return;
        project.DefaultShellId = shellId;
        if (ReferenceEquals(_workspace.SelectedProject, project))
            Terminal.Native.TerminalSessionPrewarmer.StartPrewarm(project.WorkingDirectory, project.DefaultShellId);
    }

    private void NewTabShell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _workspace.SelectedProject is not { } project) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, HorizontalOffset = button.ActualWidth - 300 };
        menu.SetResourceReference(StyleProperty, "ShellChoiceContextMenu");
        menu.Opened += (_, _) => PopulateShellChoices(menu, project, shell => { if (!project.IsDisposed) project.NewTab(shell); });
        menu.Closed += (_, _) => FocusActivePane();
        button.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ProjectShell_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace.SelectedProject is not { } project) return;
        ProjectPickerPopup.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = ProjectPillButton, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(StyleProperty, "ShellChoiceContextMenu");
        menu.Opened += (_, _) => PopulateShellChoices(menu, project, shell => SetProjectShell(project, shell), defaultOnly: true);
        menu.Closed += (_, _) => FocusActivePane();
        ProjectPillButton.ContextMenu = menu;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            if (IsLoaded && !project.IsDisposed) menu.IsOpen = true;
        });
    }

    private void PaneShellMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { DataContext: TerminalPane pane }) return;
        var project = _workspace.Projects.FirstOrDefault(project => project.Tabs.Any(tab => tab.Leaves.Contains(pane)));
        var tab = project?.Tabs.FirstOrDefault(tab => tab.Leaves.Contains(pane));
        if (project is null || tab is null) return;
        void Split(Orientation orientation, string shell)
        {
            if (project.IsDisposed || !project.Tabs.Contains(tab) || !tab.Leaves.Contains(pane)) return;
            tab.ActiveLeaf = pane;
            tab.Split(orientation, shell);
        }
        menu.Items.Clear();
        menu.Items.Add(ShellSubmenu("Split right with", project, shell => Split(Orientation.Horizontal, shell)));
        menu.Items.Add(ShellSubmenu("Split down with", project, shell => Split(Orientation.Vertical, shell)));
    }

    private void AddTabShellMenus(ContextMenu menu, WorkspaceTab tab)
    {
        var project = _workspace.Projects.FirstOrDefault(project => project.Tabs.Contains(tab));
        if (project is null) return;
        void Split(Orientation orientation, string shell)
        {
            if (!project.IsDisposed && project.Tabs.Contains(tab)) tab.Split(orientation, shell);
        }
        foreach (var previous in menu.Items.OfType<FrameworkElement>().Where(item => Equals(item.Tag, "ShellSelection")).ToArray())
            menu.Items.Remove(previous);
        menu.Items.Add(new Separator { Tag = "ShellSelection" });
        foreach (var item in new[]
        {
            ShellSubmenu("New tab with", project, shell => { if (!project.IsDisposed) project.NewTab(shell); }),
            ShellSubmenu("Split right with", project, shell => Split(Orientation.Horizontal, shell)),
            ShellSubmenu("Split down with", project, shell => Split(Orientation.Vertical, shell)),
        })
        {
            item.Tag = "ShellSelection";
            menu.Items.Add(item);
        }
    }
}
