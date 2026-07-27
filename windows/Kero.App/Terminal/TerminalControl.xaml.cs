using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Kero.App.Model;
using Kero.Terminal;
using Microsoft.Web.WebView2.Core;

namespace Kero.App.Terminal;

/// <summary>
/// One terminal surface: xterm.js in a WebView2 rendering the VT stream of a
/// <see cref="TerminalSession"/>. Together they fill the role of upstream's
/// ghostty/Alacritty surfaces. The instance is owned by its pane model and
/// only ever reparented by WPF, so PTY state and scrollback survive tab and
/// split-layout changes — same contract as upstream's surfaces.
/// </summary>
public sealed partial class TerminalControl : UserControl, IDisposable
{
    private const string VirtualHost = "kero.terminal";

    private readonly string _workingDirectory;
    private readonly List<byte[]> _pendingOutput = new();
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
        var theme = BuiltInThemes.All.FirstOrDefault(t => t.Name == AppSettings.Instance.ThemeName) ?? BuiltInThemes.KeroDark;
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
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kero", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
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
                Foreground = (System.Windows.Media.Brush)FindResource("KeroTextDim"),
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
    /// The first renderer size arrives right after page load, so the session
    /// is spawned with real geometry instead of a guessed 80x24.
    /// </summary>
    private void OnRendererSize(short columns, short rows)
    {
        if (_session is null)
        {
            // Ignore tiny initial sizes during WPF layout to prevent shell formatting bugs (e.g. nushell spamming newlines)
            if (columns < 10 || rows < 2) return;

            var session = new TerminalSession();
            session.OutputReceived += OnSessionOutput;
            session.Exited += OnSessionExited;
            
            var shellArg = AppSettings.Instance.Shell == "Nushell" ? "nu.exe" : TerminalSession.DefaultShell();
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
    
    private void OnSessionOutput(byte[] chunk)
    {
        // Raised on the PTY reader thread; the WebView2 lives on the UI thread.
        lock (_outputLock)
        {
            if (_pendingOutput.Count < 10000)
                _pendingOutput.Add(chunk);
                
            if (_outputPending) return;
            _outputPending = true;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            List<byte[]> toProcess;
            lock (_outputLock)
            {
                _outputPending = false;
                toProcess = _pendingOutput.ToList();
                _pendingOutput.Clear();
            }

            if (!_rendererReady)
            {
                // Put them back if renderer is not ready
                lock (_outputLock)
                {
                    _pendingOutput.InsertRange(0, toProcess);
                }
                return;
            }

            if (toProcess.Count > 0)
            {
                // Combine all chunks into a single byte array to reduce IPC overhead
                int totalLength = toProcess.Sum(c => c.Length);
                byte[] combined = new byte[totalLength];
                int offset = 0;
                foreach (var c in toProcess)
                {
                    Buffer.BlockCopy(c, 0, combined, offset, c.Length);
                    offset += c.Length;
                }
                PostOutput(combined);
            }
        });
    }

    private void OnSessionExited(int exitCode)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            var notice = System.Text.Encoding.UTF8.GetBytes($"\r\n\x1b[2m[process exited with code {exitCode}]\x1b[m\r\n");
            PostOutput(notice);
            ProcessExited?.Invoke(exitCode);
        });
    }

    private void FlushPendingOutput()
    {
        List<byte[]> toProcess;
        lock (_outputLock)
        {
            toProcess = _pendingOutput.ToList();
            _pendingOutput.Clear();
        }
        
        if (toProcess.Count > 0)
        {
            int totalLength = toProcess.Sum(c => c.Length);
            byte[] combined = new byte[totalLength];
            int offset = 0;
            foreach (var c in toProcess)
            {
                Buffer.BlockCopy(c, 0, combined, offset, c.Length);
                offset += c.Length;
            }
            PostOutput(combined);
        }
    }

    private void PostOutput(byte[] chunk)
    {
        if (_disposed || WebView.CoreWebView2 is null)
            return;
        var payload = JsonSerializer.Serialize(new { type = "output", data = Convert.ToBase64String(chunk) });
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
