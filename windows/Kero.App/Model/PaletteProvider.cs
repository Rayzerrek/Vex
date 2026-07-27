using System;
using System.Collections.Generic;

namespace Kero.App.Model;

public static class PaletteProvider
{
    public static IEnumerable<PaletteItem> GetItems(Workspace workspace, Action<string> uiAction)
    {
        yield return new PaletteItem("New Tab", "Open a new terminal tab", () => workspace.SelectedProject?.NewTab(), "Terminal");
        
        if (workspace.SelectedProject?.SelectedTab != null)
        {
            yield return new PaletteItem("Close Tab", "Close current terminal tab", () => {
                var project = workspace.SelectedProject;
                project.CloseTab(project.SelectedTab!);
            }, "Terminal");
            yield return new PaletteItem("Split Right", "Split current tab horizontally", () => workspace.SelectedProject.SelectedTab.Split(System.Windows.Controls.Orientation.Horizontal), "Terminal");
            yield return new PaletteItem("Split Down", "Split current tab vertically", () => workspace.SelectedProject.SelectedTab.Split(System.Windows.Controls.Orientation.Vertical), "Terminal");
        }

        yield return new PaletteItem("New Project", "Open a new project directory", () => uiAction("NewProject"), "Workspace");
        yield return new PaletteItem("Settings", "Open application settings", () => uiAction("Settings"), "App");
    }
}
