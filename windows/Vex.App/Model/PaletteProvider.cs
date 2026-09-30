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
            yield return new PaletteItem("Show All Tabs", "Open visual tab overview · Ctrl+Shift+Space", () => uiAction("TabPeek"), "Terminal");
            yield return new PaletteItem("Close Tab", "Close current terminal tab · Middle-click / Ctrl+Shift+W", () => uiAction("CloseTab"), "Terminal");
            yield return new PaletteItem("Split Right", "Split current tab horizontally · Ctrl+Shift+R", () => workspace.SelectedProject.SelectedTab.Split(System.Windows.Controls.Orientation.Horizontal), "Terminal");
            yield return new PaletteItem("Split Down", "Split current tab vertically · Ctrl+Shift+D", () => workspace.SelectedProject.SelectedTab.Split(System.Windows.Controls.Orientation.Vertical), "Terminal");
            yield return new PaletteItem("Toggle Focus Mode", "Show only the active pane", () => workspace.SelectedProject.SelectedTab.ToggleFocusMode(), "Terminal");
        }

        // Projects switch list
        if (workspace.Projects.Count > 1)
        {
            yield return new PaletteItem("Next Project", "Switch to next project · Ctrl+Shift+PageDown", () => uiAction("NextProject"), "Projects");
            yield return new PaletteItem("Previous Project", "Switch to previous project · Ctrl+Shift+PageUp", () => uiAction("PrevProject"), "Projects");
        }

        for (var i = 0; i < workspace.Projects.Count; i++)
        {
            var proj = workspace.Projects[i];
            if (proj != workspace.SelectedProject)
            {
                var shortcutHint = i < 9 ? $"Ctrl+Shift+{i + 1} · " : (i == 9 ? "Ctrl+Shift+0 · " : "");
                yield return new PaletteItem(
                    $"Switch Project: {proj.Name}",
                    $"{shortcutHint}{proj.WorkingDirectory}",
                    () => workspace.SelectedProject = proj,
                    "Projects");
            }
        }

        yield return new PaletteItem("Switch Project...", "Open project picker popover · Ctrl+Shift+O", () => uiAction("ProjectPicker"), "Projects");
        yield return new PaletteItem("New Project", "Open a new project directory", () => uiAction("NewProject"), "Workspace");

        var dark = AppSettings.Instance.IsDarkAppearance;
        yield return new PaletteItem(
            dark ? "Switch to Light Appearance" : "Switch to Dark Appearance",
            "Flip the app between the light and dark theme sets",
            () => AppSettings.Instance.SetAppearance(dark ? AppSettings.LightAppearance : AppSettings.DarkAppearance),
            "App");
        yield return new PaletteItem("Theme Picker", "Browse color themes with a live preview · Ctrl+Shift+M", () => uiAction("ThemePicker"), "App");
        yield return new PaletteItem("Settings", "Open application settings", () => uiAction("Settings"), "App");
    }
}
