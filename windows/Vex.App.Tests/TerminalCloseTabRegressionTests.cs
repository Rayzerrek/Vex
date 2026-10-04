using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Vex.App.Model;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalCloseTabRegressionTests
{
    [Theory]
    [InlineData("unused", false)]
    [InlineData("open", false)]
    [InlineData("closed", false)]
    [InlineData("unused", true)]
    [InlineData("open", true)]
    [InlineData("closed", true)]
    public void CloseFirstTab_DeferredWpfCleanupDoesNotReuseDisposedPathSearch(string pickerState, bool unload)
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            Project? project = null;
            try
            {
                project = new Project("Close regression", AppContext.BaseDirectory);
                var first = project.SelectedTab!;
                var second = project.NewTab()!;
                project.SelectedTab = first;
                var control = Assert.IsType<NativeTerminalControl>(first.ActiveLeaf!.View);
                if (pickerState != "unused")
                {
                    Assert.True(SpinWait.SpinUntil(() => control.ProcessId is not null, 5000),
                        "Terminal session did not start for the path completion regression.");
                    InvokeControlMethod(control, "OpenPathCompletion");
                    Assert.NotNull(ReadControlField(control, "_pathSearchCancellation"));
                    if (pickerState == "closed")
                        InvokeControlMethod(control, "ClosePathCompletion");
                }

                // The close shortcut calls LeafPane.Close through the same tab/model wiring.
                first.ActiveLeaf.Close();
                Assert.DoesNotContain(first, project.Tabs);
                Assert.Same(second, project.SelectedTab);

                // WPF can deliver focus loss and Unloaded after the model has disposed the view.
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    if (unload)
                        control.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    else
                        control.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,
                            Environment.TickCount, control, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
                }
                control.Dispose();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
            finally
            {
                project?.Dispose();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(15000), "Tab close/path completion cleanup test did not finish.");
        Assert.Null(threadException);
    }

    private static void InvokeControlMethod(NativeTerminalControl control, string name) =>
        typeof(NativeTerminalControl).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, null);

    private static object? ReadControlField(NativeTerminalControl control, string name) =>
        typeof(NativeTerminalControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control);
}
