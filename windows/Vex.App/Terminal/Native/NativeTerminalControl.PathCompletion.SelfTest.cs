#if DEBUG || VEX_SELFTEST
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    /// <summary>Exercises path completion in the real WPF surface, including queued PTY bytes and alternate-screen prompts.</summary>
    internal void SelfTestPathCompletion(Action capture)
    {
        var root = Path.Combine(Path.GetTempPath(), "vex-path-renderer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var file = Path.Combine(root, "docs", "read me.md");
        File.WriteAllText(file, "");
        File.WriteAllText(Path.Combine(root, "NativeTerminalControl.cs"), "");
        var originalShell = _sessionShellId;
        try
        {
            _sessionShellId = "pwsh";
            SelfTestFeed("\x1b[2J\x1b[HPS " + root + "> cat ");
            HandlePathCompletionKey(Key.F, ModifierKeys.Control | ModifierKeys.Shift);
            WaitForPathCompletion(() => _pathCompletionMatches.Length == 2);
            if (!_pathCompletionOpen || _pathCompletionDirectory != root)
                throw new InvalidOperationException("Path completion did not open at the shell working directory.");
            TypePathCompletionQuery("ntc");
            TypePathCompletionQuery("nomatch");
            HandlePathCompletionKey(Key.Back, ModifierKeys.Control);
            TypePathCompletionQuery("ntc");
            WaitForPathCompletion(() => _pathMatchedQuery == "ntc" && _pathCompletionMatches.Length == 1);
            if (!_pathCompletionMatches[0].RelativePath.Equals("NativeTerminalControl.cs"))
                throw new InvalidOperationException("Path completion published a stale query result.");
            HandlePathCompletionKey(Key.Escape, ModifierKeys.None);
            if (_pathCompletionOpen || _pathCompletionVisual.Drawing is { } drawing && drawing.Bounds != Rect.Empty)
                throw new InvalidOperationException("Path completion left pixels after Escape.");

            HandlePathCompletionKey(Key.F, ModifierKeys.Control | ModifierKeys.Shift);
            TypePathCompletionQuery("docs");
            WaitForPathCompletion(() => _pathMatchedQuery == "docs" && _pathCompletionMatches.FirstOrDefault() is { RelativePath: "docs", IsDirectory: true });
            HandlePathCompletionKey(Key.Tab, ModifierKeys.None);
            WaitForPathCompletion(() => _pathCompletionMatches.Length == 1 && _pathMatchedQuery == "");
            if (_pathCompletionDirectory != Path.Combine(root, "docs"))
                throw new InvalidOperationException("Path completion did not navigate into the selected directory.");
            HandlePathCompletionKey(Key.Tab, ModifierKeys.Shift);
            WaitForPathCompletion(() => _pathCompletionMatches.Length == 2 && _pathMatchedQuery == "");
            TypePathCompletionQuery("rdm");
            WaitForPathCompletion(() => _pathMatchedQuery == "rdm" && _pathCompletionMatches.Length == 1);
            if (!_pathCompletionBounds.IntersectsWith(new Rect(0, 0, ActualWidth, ActualHeight)))
                throw new InvalidOperationException("Path completion was drawn outside the pane.");
            capture();

            _sessionStarting = true;
            _pendingSessionInput = null;
            HandlePathCompletionKey(Key.Enter, ModifierKeys.None);
            if (_pathCompletionOpen || _pendingSessionInput is not { Count: 1 } ||
                Encoding.UTF8.GetString(_pendingSessionInput[0]) != "'" + file + "'")
                throw new InvalidOperationException("Path completion did not paste exactly one quoted path without Enter.");
            _sessionStarting = false;
            _pendingSessionInput = null;

            // Full-screen programs must receive raw paths even when the parent shell quotes arguments.
            SelfTestFeed("\x1b[?1049h\x1b[2J\x1b[Happlication> ");
            HandlePathCompletionKey(Key.F, ModifierKeys.Control | ModifierKeys.Shift);
            ChangePathCompletionDirectory(root);
            TypePathCompletionQuery("rdm");
            WaitForPathCompletion(() => _pathMatchedQuery == "rdm" && _pathCompletionMatches.Length == 1);
            _sessionStarting = true;
            HandlePathCompletionKey(Key.Enter, ModifierKeys.None);
            if (_pendingSessionInput is not { Count: 1 } || Encoding.UTF8.GetString(_pendingSessionInput[0]) != file)
                throw new InvalidOperationException("Path completion did not insert a raw path into the alternate screen.");
            _sessionStarting = false;
            _pendingSessionInput = null;
            SelfTestFeed("\x1b[?1049l");
        }
        finally
        {
            ClosePathCompletion();
            _sessionStarting = false;
            _pendingSessionInput = null;
            _sessionShellId = originalShell;
            _pathCompletionIndex?.Dispose();
            _pathCompletionIndex = null;
            Directory.Delete(root, recursive: true);
        }
    }

    private void WaitForPathCompletion(Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = Environment.TickCount64 + 10_000;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) =>
        {
            if (ready() || Environment.TickCount64 >= deadline)
                frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        if (!ready())
            throw new TimeoutException("Path completion renderer self-test timed out.");
    }
}
#endif
