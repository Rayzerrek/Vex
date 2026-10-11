using System.Windows;
using System.Windows.Threading;
using Vex.Terminal;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private TerminalClipboardListener? _clipboardListener;
    private uint _lastCopyClipboardSequence;
    private DispatcherTimer? _clipboardRetryTimer;
    private int _clipboardReadAttempts;

    private void StartClipboardListener()
    {
        _lastCopyClipboardSequence = TerminalClipboardListener.SequenceNumber;
        _clipboardListener ??= new TerminalClipboardListener(OnApplicationClipboardChanged);
    }

    private void StopClipboardListener()
    {
        _clipboardRetryTimer?.Stop();
        _clipboardListener?.Dispose();
        _clipboardListener = null;
    }

    private void OnApplicationClipboardChanged(uint sequence, uint ownerProcessId)
    {
        if (sequence == _lastCopyClipboardSequence)
            return;
        _lastCopyClipboardSequence = sequence;
        _clipboardRetryTimer?.Stop();
        if (!CanShowApplicationCopyFeedback() || ownerProcessId == Environment.ProcessId)
            return;
        // A background desktop application must not flash the focused terminal.
        // Null owners occur with clipboard helpers that open the clipboard without a window.
        if (ownerProcessId != 0 && ProcessId is { } rootPid)
        {
            var entries = ProcessTree.Snapshot();
            if (entries is null)
                return;
            var parents = entries.ToDictionary(entry => entry.Pid, entry => entry.ParentPid);
            var pid = ownerProcessId;
            var visited = new HashSet<uint>();
            while (pid != (uint)rootPid && visited.Add(pid) && parents.TryGetValue(pid, out var parent))
                pid = parent;
            if (pid != (uint)rootPid)
                return;
        }
        _clipboardReadAttempts = 0;
        ReadApplicationClipboard();
    }

    private bool CanShowApplicationCopyFeedback() =>
        !_disposed && IsLoaded && IsVisible && IsKeyboardFocusWithin && Window.GetWindow(this)?.IsActive == true;

    private void ReadApplicationClipboard()
    {
        _clipboardRetryTimer?.Stop();
        if (!CanShowApplicationCopyFeedback() || TerminalClipboardListener.SequenceNumber != _lastCopyClipboardSequence)
            return;
        if (TryGetClipboardText(out var text))
        {
            if (text.Length > 0)
                ShowApplicationCopyFeedback(text);
            return;
        }
        if (++_clipboardReadAttempts >= 5)
            return;
        if (_clipboardRetryTimer is null)
        {
            _clipboardRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _clipboardRetryTimer.Tick += (_, _) => ReadApplicationClipboard();
        }
        _clipboardRetryTimer.Start();
    }

    private void OnApplicationClipboardWrite(string text)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || !TrySetClipboardText(text))
                return;
            _lastCopyClipboardSequence = TerminalClipboardListener.SequenceNumber;
            if (IsLoaded && IsVisible)
                ShowApplicationCopyFeedback(text);
        });
    }

    private void ShowApplicationCopyFeedback(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _disposed || !Monitor.TryEnter(_terminal.SyncRoot))
            return;
        try
        {
            if (_terminal.SynchronizedOutput)
                return;
            // Clipboard notifications may beat the queued output repaint.
            // Match the current painted frame, not text the editor just removed.
            FlushRedraw();
            var ranges = TerminalCopyTextMatcher.FindCopyRanges(_terminal.FrameRows, _cols, text, _terminal.Cursor.Y);
            // Clipboard writes also represent editor deletes/cuts. Without a
            // visible source, a caret flash would falsely imply a copy operation.
            if (ranges.Count == 0)
                return;
            StartCopyAnimation(ranges);
        }
        finally { Monitor.Exit(_terminal.SyncRoot); }
    }
}
