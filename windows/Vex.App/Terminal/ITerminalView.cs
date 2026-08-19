namespace Vex.App.Terminal;

/// <summary>
/// Terminal pane surface contract. The pane model depends on this so future
/// native renderers can replace the current WPF surface without changing
/// workspace/session lifetime.
/// </summary>
public interface ITerminalView : IDisposable
{
    event Action<string>? TitleChanged;

    /// <summary>Raw OSC 0/2 title, before the pane model derives the tab text.</summary>
    event Action<string>? TitleRawChanged;
    event Action<int>? ProcessExited;
    event Action? FocusGained;
    event Action<TerminalCommand>? CommandRequested;

    /// <summary>Raised when the terminal enters or leaves the alternate screen
    /// buffer (a full-screen TUI starts or exits). The boolean is true while a
    /// TUI is active. Used to surface pane state in the title bar.</summary>
    event Action<bool>? TuiModeChanged;

    /// <summary>PID of the ConPTY shell; null until the session starts.</summary>
    int? ProcessId { get; }

    void FocusTerminal();
}
