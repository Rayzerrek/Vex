namespace Vex.App.Terminal;

/// <summary>
/// Terminal pane surface contract. The pane model depends on this so future
/// native renderers can replace the current WPF surface without changing
/// workspace/session lifetime.
/// </summary>
public interface ITerminalView : IDisposable
{
    event Action<string>? TitleChanged;
    event Action<int>? ProcessExited;
    event Action? FocusGained;
    event Action<TerminalCommand>? CommandRequested;

    void FocusTerminal();
}
