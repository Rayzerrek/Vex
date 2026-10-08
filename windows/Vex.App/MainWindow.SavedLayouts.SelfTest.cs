#if DEBUG || VEX_SELFTEST
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.Libghostty;

namespace Vex.App;

public sealed partial class MainWindow
{
    internal bool SelfTestSavedLayouts()
    {
        var project = _workspace.SelectedProject!;
        var originalTab = project.SelectedTab!;
        var layout = SavedTerminalLayouts.CaptureLayout(originalTab, project.WorkingDirectory);
        layout.Name = "Vex layout self-test";
        SavedLayoutOverlay.Show(layout);
        Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        try
        {
            var editor = _savedLayoutOverlay!.SelfTestLayoutEditor();
            OnDeactivated(EventArgs.Empty);
            var frame = new DispatcherFrame();
            var deadline = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
            deadline.Start();
            Dispatcher.PushFrame(frame);
            editor &= _savedLayoutOverlay.IsVisible && _savedLayoutOverlay.SelfTestLayoutEditor();

            var leaf = originalTab.ActiveLeaf!;
            var previousStatus = leaf.ProgramStatus;
            try
            {
                leaf.ProgramStatus = new(ProgramStatusState.Blocked, Kind: "permission", Message: "Approve this operation");
                Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var badge = FindProgramStatusBadges(TabStrip).Single(item => ReferenceEquals(item.DataContext, originalTab));
                editor &= badge.Status.State == ProgramStatusState.Blocked && badge.Origin == originalTab.ProgramStatusOrigin
                    && (badge.ToolTip as string)?.StartsWith(badge.Origin + "\n", StringComparison.Ordinal) == true;
            }
            finally { leaf.ProgramStatus = previousStatus; }
            var settings = AppSettings.Instance;
            settings.SavedLayouts.Add(layout);
            try
            {
                var header = (Grid)((DataTemplate)Resources["TabHeaderTemplate"]).LoadContent();
                var menu = header.ContextMenu;
                menu.PlacementTarget = ProjectPillButton;
                menu.StaysOpen = true;
                menu.IsOpen = true;
                Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                try
                {
                    var layoutsMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "SavedLayouts"));
                    layoutsMenu.IsSubmenuOpen = true;
                    layoutsMenu.ApplyTemplate();
                    Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    var popup = layoutsMenu.Template.FindName("PART_Popup", layoutsMenu) as Popup;
                    if (popup is not { IsOpen: true })
                        throw new InvalidOperationException($"Saved layout submenu did not open: menuOpen={menu.IsOpen}, submenuOpen={layoutsMenu.IsSubmenuOpen}, popup={popup?.IsOpen}");
                    return editor && layoutsMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, layout.Name));
                }
                finally { menu.IsOpen = false; }
            }
            finally { settings.SavedLayouts.Remove(layout); }
        }
        finally { _savedLayoutOverlay!.Hide(); }
    }

    private static IEnumerable<ProgramStatusBadge> FindProgramStatusBadges(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ProgramStatusBadge badge) yield return badge;
            else foreach (var nested in FindProgramStatusBadges(child)) yield return nested;
        }
    }
}
#endif
