#if DEBUG || VEX_SELFTEST
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Vex.App.Model;

namespace Vex.App;

public sealed partial class MainWindow
{
    internal bool SelfTestShellSelection()
    {
        if (_workspace.SelectedProject is not { IsWsl: false } project || project.SelectedTab is not { } originalTab)
            return true;
        var originalDefault = project.DefaultShellId;
        var originalTabs = project.Tabs.ToArray();
        var originalShells = originalTab.Leaves.OfType<TerminalPane>().ToDictionary(pane => pane, pane => pane.ShellId);
        try
        {
            NewTabShell_Click(NewTabShellButton, new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (NewTabShellButton.ContextMenu is not { IsOpen: true } menu)
                throw new InvalidOperationException("Terminal shell dropdown did not open.");
            var defaultChoice = menu.Items.OfType<MenuItem>().Single(item => item.Header is ShellMenuChoice { IsDefault: true });
            defaultChoice.ApplyTemplate();
            if (defaultChoice.Template.FindName("activeBar", defaultChoice) is not Border { Visibility: Visibility.Visible })
                throw new InvalidOperationException("Shell list did not use the project picker's active-row styling.");
            var powershellId = ShellRegistry.HasDetected("pwsh") ? "pwsh" : "powershell";
            if (menu.Items.OfType<MenuItem>().Count(item => item.Header is ShellMenuChoice) > 3
                || menu.Items.OfType<MenuItem>().Any(item => item.Header is string title && title.StartsWith("Project default")))
                throw new InvalidOperationException("Terminal shell list is cluttered with advanced choices or project settings.");
            var powershell = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, powershellId));
            powershell.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            if (project.SelectedTab is not { } tab || tab.ActiveLeaf is not TerminalPane powershellPane || powershellPane.ShellId != powershellId)
                throw new InvalidOperationException("Terminal shell dropdown did not create the selected PowerShell pane.");
            var root = tab.Root;
            var splitMenu = new ContextMenu { PlacementTarget = NewTabShellButton, StaysOpen = true };
            AddTabShellMenus(splitMenu, tab);
            AddTabShellMenus(splitMenu, tab);
            if (splitMenu.Items.OfType<MenuItem>().Count(item => Equals(item.Header, "Split right with")) != 1)
                throw new InvalidOperationException("Repeated tab menus duplicate shell actions.");
            var split = splitMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Split right with"));
            splitMenu.IsOpen = true;
            split.IsSubmenuOpen = true;
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            var commandPromptId = originalDefault == ShellRegistry.SystemDefaultId ? "system" : "cmd";
            var commandPrompt = split.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, commandPromptId));
            if (commandPrompt is null)
            {
                var moreShells = split.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "More shells"));
                moreShells.IsSubmenuOpen = true;
                commandPrompt = moreShells.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, commandPromptId));
            }
            commandPrompt.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            splitMenu.IsOpen = false;
            if (tab.Root is not SplitPane tree || !ReferenceEquals(tree.First, root) || tab.ActiveLeaf is not TerminalPane commandPane || commandPane.ShellId != commandPromptId)
                throw new InvalidOperationException("Split shell submenu did not preserve the original pane and selected shell.");
            project.DefaultShellId = powershellId;
            ProjectShell_Click(ProjectPillButton, new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            if (ProjectPillButton.ContextMenu is not { IsOpen: true } defaults)
                throw new InvalidOperationException("Project shell settings did not open separately from the terminal list.");
            var more = defaults.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "More shells"));
            more.IsSubmenuOpen = true;
            more.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "cmd"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            defaults.IsOpen = false;
            if (project.DefaultShellId != "cmd" || powershellPane.ShellId != powershellId || originalShells.Any(pair => pair.Key.ShellId != pair.Value))
                throw new InvalidOperationException("Project shell settings changed an existing pane or ignored the new default.");
            if (project.NewTab()?.ActiveLeaf is not TerminalPane { ShellId: "cmd" })
                throw new InvalidOperationException("New terminal did not use the changed project default.");
            return new WslProjectOverlay().SelfTestChoiceSelectors();
        }
        finally
        {
            if (NewTabShellButton.ContextMenu is { } menu) menu.IsOpen = false;
            if (ProjectPillButton.ContextMenu is { } defaults) defaults.IsOpen = false;
            project.DefaultShellId = originalDefault;
            project.SelectedTab = originalTab;
            foreach (var tab in project.Tabs.Except(originalTabs).ToArray()) project.CloseTab(tab);
        }
    }
}
#endif
