using System;
using System.Collections.Generic;

namespace Vex.App.Model;

public static class PaletteProvider
{
    public static IEnumerable<PaletteItem> GetItems(Workspace workspace, Action<string> uiAction)
    {
        yield return new PaletteItem("New Tab", "Open a new terminal tab · Ctrl+Shift+T", () => workspace.SelectedProject?.NewTab(), "Terminal");
        
        if (workspace.SelectedProject?.SelectedTab != null)
        {
            yield return new PaletteItem("Close Tab", "Close current terminal tab · Middle-click / Ctrl+Shift+W", () => {
                var project = workspace.SelectedProject;
                project.CloseTab(project.SelectedTab!);
            }, "Terminal");
            yield return new PaletteItem("Split Right", "Split current tab horizontally · Ctrl+Shift+Right", () => workspace.SelectedProject.SelectedTab.Split(System.Windows.Controls.Orientation.Horizontal), "Terminal");
            yield return new PaletteItem("Split Down", "Split current tab vertically · Ctrl+Shift+Down", () => workspace.SelectedProject.SelectedTab.Split(System.Windows.Controls.Orientation.Vertical), "Terminal");
            yield return new PaletteItem("Toggle Focus Mode", "Show only the active pane", () => workspace.SelectedProject.SelectedTab.ToggleFocusMode(), "Terminal");

            if (workspace.SelectedProject.SelectedTab.ActiveLeaf is EditorPane editorPane)
            {
                yield return new PaletteItem("Save File", "Save current open file · Ctrl+S", () => editorPane.Save(), "Editor");
            }
        }

        // Projects switch list
        foreach (var proj in workspace.Projects)
        {
            if (proj != workspace.SelectedProject)
            {
                yield return new PaletteItem(
                    $"Switch Project: {proj.Name}",
                    proj.WorkingDirectory,
                    () => workspace.SelectedProject = proj,
                    "Projects");
            }
        }

        yield return new PaletteItem("Toggle Sidebar", "Show or hide the workspace sidebar", () => AppSettings.Instance.SidebarVisible = !AppSettings.Instance.SidebarVisible, "Workspace");
        yield return new PaletteItem("New Project", "Open a new project directory", () => uiAction("NewProject"), "Workspace");
        yield return new PaletteItem("Theme Picker", "Browse color themes with a live preview · Ctrl+Shift+M", () => uiAction("ThemePicker"), "App");
        yield return new PaletteItem("Settings", "Open application settings", () => uiAction("Settings"), "App");
    }
}
