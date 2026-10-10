using System.Diagnostics;
using System.Windows.Threading;
using Vex.App.Terminal.Native;
using Xunit;

namespace Vex.App.Tests;

[Collection("CustomTheme")]
public sealed class TerminalClipboardListenerTests
{
    [Fact]
    public void ExternalProcessCopy_WindowsNotificationDeliversTextAndSequence()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var hadText = NativeTerminalControl.TryGetClipboardText(out var originalText);
            try
            {
                const string text = "Vex external clipboard regression";
                var frame = new DispatcherFrame();
                var received = false;
                var previousSequence = TerminalClipboardListener.SequenceNumber;
                using var listener = new TerminalClipboardListener((sequence, owner) =>
                {
                    if (NativeTerminalControl.TryGetClipboardText(out var copied) && copied == text)
                    {
                        received = sequence != previousSequence;
                        frame.Continue = false;
                    }
                });
                var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                timeout.Tick += (_, _) => frame.Continue = false;
                timeout.Start();
                var start = new ProcessStartInfo("pwsh.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add($"Set-Clipboard -Value '{text}'");
                using var process = Process.Start(start)!;
                try
                {
                    Dispatcher.PushFrame(frame);
                    Assert.True(received, "External clipboard write did not produce WM_CLIPBOARDUPDATE with copied text");
                }
                finally
                {
                    timeout.Stop();
                    if (!process.WaitForExit(5000))
                        process.Kill();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (hadText)
                    NativeTerminalControl.TrySetClipboardText(originalText);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Clipboard notification test did not finish");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
