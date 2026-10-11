using System.Windows;
using System.Windows.Threading;
using Vex.App.Model;
using Xunit;

namespace Vex.App.Tests;

public sealed class TabPeekPreviewTests
{
    [Fact]
    public void Overview_TracksLayoutsWithoutRealizingPanesAndStopsRefreshingOnClose()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                using var project = new Project("Project", Environment.CurrentDirectory);
                var tab = Assert.IsType<WorkspaceTab>(project.SelectedTab);
                using var preview = new TabPeekPreview(tab);
                var originalLayout = preview.Layout;
                tab.Split(System.Windows.Controls.Orientation.Horizontal);
                Assert.NotSame(originalLayout, preview.Layout);
                preview.RefreshTerminalPreviews();
                Assert.All(tab.Leaves, pane => Assert.Null(pane.ExistingView));
                var peek = new TabPeek();
                ChromePalette.Apply(BuiltInThemes.VexDark, peek.Resources);
                var host = new System.Windows.Controls.Grid();
                host.Children.Add(peek);
                window = new Window { Content = host, Width = 950, Height = 650, Left = -10000, ShowActivated = false, ShowInTaskbar = false };
                window.Show();
                peek.Show(project);
                PumpDispatcher(200);
                Assert.True(peek.IsRefreshingPreviews);
                Assert.All(tab.Leaves, pane => Assert.Null(pane.ExistingView));
                project.NewTab();
                PumpDispatcher(150);
                peek.Hide();
                Assert.False(peek.IsRefreshingPreviews);
                PumpDispatcher(200);
                Assert.Equal(Visibility.Collapsed, peek.Visibility);
                peek.Show(project);
                PumpDispatcher(100);
                var list = Assert.IsType<System.Windows.Controls.ListBox>(peek.FindName("TabList"));
                var item = Assert.IsType<System.Windows.Controls.ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                DependencyObject? ancestor = item;
                while (ancestor is not null && ancestor is not System.Windows.Controls.ScrollViewer)
                    ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ancestor);
                var scroll = Assert.IsType<System.Windows.Controls.ScrollViewer>(ancestor);
                var presenter = Assert.IsType<System.Windows.Controls.ItemsPresenter>(scroll.Content);
                scroll.Content = null;
                host.Children.Add(presenter);
                window.UpdateLayout();
                Assert.Same(item, list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(item.IsVisible);
                Assert.False(list.IsAncestorOf(item));
                var refresh = (typeof(TabPeek).GetMethod("RefreshVisiblePreviews", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Tab Peek refresh callback was not found.")).CreateDelegate<Action>(peek);
                refresh();
                window.Close();
                Assert.False(peek.IsRefreshingPreviews);
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(15000), "Tab Peek preview lifecycle test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void PumpDispatcher(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
