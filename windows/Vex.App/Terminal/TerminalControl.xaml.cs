using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Vex.App.Model;
using Vex.Terminal;
using Microsoft.Web.WebView2.Core;

namespace Vex.App.Terminal;

/// <summary>
/// One terminal surface: xterm.js in a WebView2 rendering the VT stream of a
/// <see cref="TerminalSession"/>. Together they fill the role of upstream's
/// ghostty/Alacritty surfaces. The instance is owned by its pane model and
/// only ever reparented by WPF, so PTY state and scrollback survive tab and
/// split-layout changes — same contract as upstream's surfaces.
/// </summary>
public sealed partial class TerminalControl : UserControl, ITerminalView
{
    private const string VirtualHost = "Vex.terminal";
    private static readonly object WebViewEnvironmentLock = new();
    private static Task<CoreWebView2Environment>? _sharedWebViewEnvironmentTask;

    private readonly string _workingDirectory;
    private List<ArraySegment<byte>> _pendingOutput = new();
    private List<ArraySegment<byte>> _processingOutput = new();
    private TerminalSession? _session;
    private bool _rendererReady;
    private bool _disposed;

    public event Action<string>? TitleChanged;
    public event Action<int>? ProcessExited;
    public event Action? FocusGained;
    public event Action<TerminalCommand>? CommandRequested;

    public TerminalControl(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => TryStartSessionFromControlSize();
        WebView.GotFocus += (_, _) => FocusGained?.Invoke();
        WebView.GotKeyboardFocus += (_, _) => FocusGained?.Invoke();
        AppSettings.Instance.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_rendererReady)
            ApplySettings();
    }

    private void ApplySettings()
    {
        if (_disposed || WebView.CoreWebView2 is null) return;
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName) ?? BuiltInThemes.VexDark;
        var payload = JsonSerializer.Serialize(new
        {
            type = "theme",
            fontFamily = AppSettings.Instance.FontFamily,
            fontSize = AppSettings.Instance.FontSize,
            cursorBlink = AppSettings.Instance.CursorBlink,
            theme = new
            {
                background = theme.Background,
                foreground = theme.Foreground,
                cursor = theme.Cursor,
                selectionBackground = theme.SelectionBackground,
                black = theme.Black,
                red = theme.Red,
                green = theme.Green,
                yellow = theme.Yellow,
                blue = theme.Blue,
                magenta = theme.Magenta,
                cyan = theme.Cyan,
                white = theme.White,
                brightBlack = theme.BrightBlack,
                brightRed = theme.BrightRed,
                brightGreen = theme.BrightGreen,
                brightYellow = theme.BrightYellow,
                brightBlue = theme.BrightBlue,
                brightMagenta = theme.BrightMagenta,
                brightCyan = theme.BrightCyan,
                brightWhite = theme.BrightWhite
            }
        });
        WebView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    public void FocusTerminal() => WebView.Focus();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            WebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            var environment = await GetSharedWebViewEnvironmentAsync();
            await WebView.EnsureCoreWebView2Async(environment);

            var assetsFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "terminal");
            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, assetsFolder, CoreWebView2HostResourceAccessKind.Allow);
            WebView.CoreWebView2.WebMessageReceived += OnWebMessage;
            WebView.CoreWebView2.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Root.Children.Clear();
            Root.Children.Add(new TextBlock
            {
                Text = "Microsoft WebView2 runtime is not installed.",
                Foreground = (System.Windows.Media.Brush)FindResource("VexTextDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var message = JsonDocument.Parse(e.WebMessageAsJson);
        var root = message.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _rendererReady = true;
                ApplySettings();
                TryStartSessionFromControlSize();
                FlushPendingOutput();
                break;
            case "resize":
            case "size":
                var columns = (short)root.GetProperty("cols").GetInt32();
                var rows = (short)root.GetProperty("rows").GetInt32();
                if (columns > 0 && rows > 0)
                    OnRendererSize(columns, rows);
                break;
            case "input":
                var data = Convert.FromBase64String(root.GetProperty("data").GetString()!);
                _session?.Write(data);
                break;
            case "title":
                TitleChanged?.Invoke(root.GetProperty("title").GetString() ?? "");
                break;
            case "focus":
                FocusGained?.Invoke();
                break;
            case "paste":
                if (_session != null && Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
                    _session.Write(System.Text.Encoding.UTF8.GetBytes(text));
                }
                break;
            case "command":
                var command = root.GetProperty("name").GetString() switch
                {
                    "splitRight" => TerminalCommand.SplitRight,
                    "splitDown" => TerminalCommand.SplitDown,
                    "newTab" => TerminalCommand.NewTab,
                    "closePane" => TerminalCommand.ClosePane,
                    _ => (TerminalCommand?)null,
                };
                if (command is { } requested)
                    CommandRequested?.Invoke(requested);
                break;
        }
    }

    /// <summary>
    /// New tabs used to wait for xterm.js to report its geometry before the
    /// shell could start. If WebView2 startup stalls, that can delay (or
    /// prevent) shell launch. Start from the control size as a fallback, then
    /// let later xterm resize events correct the final geometry.
    /// </summary>
    private void TryStartSessionFromControlSize()
    {
        if (_session is not null)
            return;
        if (!TryEstimateGridSize(out var columns, out var rows))
            return;
        OnRendererSize(columns, rows);
    }

    private bool TryEstimateGridSize(out short columns, out short rows)
    {
        columns = 0;
        rows = 0;
        if (!IsLoaded || ActualWidth < 150 || ActualHeight < 80)
            return false;

        // index.html adds 6px left and 2px top padding.
        var width = Math.Max(0, ActualWidth - 6);
        var height = Math.Max(0, ActualHeight - 2);
        var fontSize = Math.Max(8, AppSettings.Instance.FontSize);
        // xterm's fallback metrics are close to these ratios for monospace fonts.
        var cellWidth = Math.Max(6.0, fontSize * 0.62);
        var cellHeight = Math.Max(10.0, fontSize * 1.35);

        var cols = (int)Math.Floor(width / cellWidth);
        var r = (int)Math.Floor(height / cellHeight);
        if (cols <= 0 || r <= 0)
            return false;

        columns = (short)Math.Clamp(cols, short.MinValue, short.MaxValue);
        rows = (short)Math.Clamp(r, short.MinValue, short.MaxValue);
        return true;
    }

    private static Task<CoreWebView2Environment> GetSharedWebViewEnvironmentAsync()
    {
        lock (WebViewEnvironmentLock)
        {
            if (_sharedWebViewEnvironmentTask is null)
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Vex", "WebView2");
                _sharedWebViewEnvironmentTask = CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);
            }
            return _sharedWebViewEnvironmentTask;
        }
    }

    /// <summary>
    /// The first renderer size arrives right after page load, so the session
    /// is spawned with real geometry instead of a guessed 80x24.
    /// </summary>
    private void OnRendererSize(short columns, short rows)
    {
        if (_session is null)
        {
            // Ignore tiny initial sizes during WPF layout to prevent shell formatting bugs (e.g. nushell spamming newlines)
            if (columns < 20 || rows < 5) return;

            var session = new TerminalSession();
            session.OutputReceived += OnSessionOutput;
            session.Exited += OnSessionExited;
            
            var shellArg = AppSettings.Instance.Shell switch
            {
                "Nushell" => "nu.exe",
                "PowerShell" => TerminalSession.PowerShell(),
                _ => TerminalSession.DefaultShell()
            };
            session.Start(_workingDirectory, columns, rows, shellArg);
            _session = session;
        }
        else
        {
            _session.Resize(columns, rows);
        }
    }

    private readonly object _outputLock = new();
    private bool _outputPending;
    
    private void OnSessionOutput(ArraySegment<byte> chunk)
    {
        // Raised on the PTY reader thread; the WebView2 lives on the UI thread.
        lock (_outputLock)
        {
            if (_pendingOutput.Count < 10000)
                _pendingOutput.Add(chunk);
                
            if (_outputPending) return;
            _outputPending = true;
        }

        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
        {
            List<ArraySegment<byte>> toProcess;
            lock (_outputLock)
            {
                _outputPending = false;
                toProcess = _pendingOutput;
                _pendingOutput = _processingOutput;
                _processingOutput = toProcess;
            }

            if (!_rendererReady)
            {
                // Put them back if renderer is not ready
                lock (_outputLock)
                {
                    _pendingOutput.InsertRange(0, toProcess);
                }
                toProcess.Clear();
                return;
            }

            if (toProcess.Count > 0)
            {
                // Combine all chunks into a single byte array to reduce IPC overhead
                int totalLength = toProcess.Sum(c => c.Count);
                byte[] combined = System.Buffers.ArrayPool<byte>.Shared.Rent(totalLength);
                int offset = 0;
                foreach (var c in toProcess)
                {
                    Buffer.BlockCopy(c.Array!, c.Offset, combined, offset, c.Count);
                    offset += c.Count;
                    System.Buffers.ArrayPool<byte>.Shared.Return(c.Array!);
                }
                PostOutput(combined, totalLength);
                System.Buffers.ArrayPool<byte>.Shared.Return(combined);
                toProcess.Clear();
            }
        });
    }

    private void OnSessionExited(int exitCode)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var notice = System.Text.Encoding.UTF8.GetBytes($"\r\n\x1b[2m[process exited with code {exitCode}]\x1b[m\r\n");
            PostOutput(notice, notice.Length);
            ProcessExited?.Invoke(exitCode);
        });
    }

    private void FlushPendingOutput()
    {
        List<ArraySegment<byte>> toProcess;
        lock (_outputLock)
        {
            toProcess = _pendingOutput;
            _pendingOutput = _processingOutput;
            _processingOutput = toProcess;
        }
        
        if (toProcess.Count > 0)
        {
            int totalLength = toProcess.Sum(c => c.Count);
            byte[] combined = System.Buffers.ArrayPool<byte>.Shared.Rent(totalLength);
            int offset = 0;
            foreach (var c in toProcess)
            {
                Buffer.BlockCopy(c.Array!, c.Offset, combined, offset, c.Count);
                offset += c.Count;
                System.Buffers.ArrayPool<byte>.Shared.Return(c.Array!);
            }
            PostOutput(combined, totalLength);
            System.Buffers.ArrayPool<byte>.Shared.Return(combined);
            toProcess.Clear();
        }
    }

    private void PostOutput(byte[] chunk, int length)
    {
        if (_disposed || WebView.CoreWebView2 is null)
            return;
        var base64 = Convert.ToBase64String(chunk, 0, length);
        var payload = $"{{\"type\":\"output\",\"data\":\"{base64}\"}}";
        WebView.CoreWebView2.PostWebMessageAsJson(payload);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AppSettings.Instance.PropertyChanged -= OnSettingsChanged;
        _session?.Dispose();
        WebView.Dispose();
    }
}
