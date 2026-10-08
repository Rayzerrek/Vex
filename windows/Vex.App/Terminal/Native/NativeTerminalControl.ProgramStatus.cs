using System.Windows.Threading;
using Vex.Libghostty;

namespace Vex.App.Terminal.Native;

public sealed partial class NativeTerminalControl
{
    private int _programStatusDispatchPending;
    private int _programStatusAttentionPending;
    private DispatcherTimer? _programStatusTimer;

    /// <summary>Program status changes are batched independently of terminal rendering and visibility.</summary>
    public event Action<ProgramStatusSummary, bool>? ProgramStatusChanged;

    private void QueueProgramStatus(ProgramStatusSummary summary, bool attention)
    {
        if (attention)
            Interlocked.Exchange(ref _programStatusAttentionPending, 1);
        if (Interlocked.Exchange(ref _programStatusDispatchPending, 1) == 1)
            return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            // Only a pending status starts this one-shot timer. Even a report flood
            // updates headers at most ten times a second and idle panes have no ticks.
            if (_programStatusTimer is null)
            {
                _programStatusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
                _programStatusTimer.Tick += (_, _) =>
                {
                    _programStatusTimer.Stop();
                    Interlocked.Exchange(ref _programStatusDispatchPending, 0);
                    var needsAttention = Interlocked.Exchange(ref _programStatusAttentionPending, 0) != 0;
                    if (!_disposed) ProgramStatusChanged?.Invoke(_terminal.ProgramStatus, needsAttention);
                };
            }
            _programStatusTimer.Start();
        }, DispatcherPriority.Background);
    }

#if DEBUG || VEX_SELFTEST
    internal bool SelfTestProgramStatusBatch()
    {
        var updates = 0;
        var attentionSeen = false;
        void ObserveStatus(ProgramStatusSummary status, bool attention) { updates++; attentionSeen |= attention; }
        ProgramStatusChanged += ObserveStatus;
        try
        {
            for (var i = 0; i < 1000; i++)
                _terminal.Feed($"\x1b]7501;state=working:progress={i % 101}\x07");
            _terminal.Feed("\x1b]7501;state=blocked:kind=question\x07\x1b]7501;state=working:progress=99\x07");
            var frame = new DispatcherFrame();
            var deadline = new DispatcherTimer(DispatcherPriority.ContextIdle, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            deadline.Tick += (_, _) => { deadline.Stop(); frame.Continue = false; };
            deadline.Start();
            Dispatcher.PushFrame(frame);
            return updates == 1 && attentionSeen && _terminal.ProgramStatus.Progress == 99;
        }
        finally
        {
            ProgramStatusChanged -= ObserveStatus;
            _terminal.Feed("\x1b]7501;state=clear\x07");
        }
    }
#endif
}
