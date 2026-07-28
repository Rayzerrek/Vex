namespace Vex.App.Terminal;

/// <summary>
/// One terminal surface regardless of the rendering backend. Implemented by
/// <see cref="TerminalControl"/> (xterm.js in WebView2) and
/// <see cref="Native.NativeTerminalControl"/> (XtermSharp + WPF renderer);
/// panes program against this so the backend is swappable from settings.
/// </summary>
public interface ITerminalView : IDisposable
{
    event Action<string>? TitleChanged;
    event Action<int>? ProcessExited;
    event Action? FocusGained;
    event Action<TerminalCommand>? CommandRequested;

    void FocusTerminal();
}
