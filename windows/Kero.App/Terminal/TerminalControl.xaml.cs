using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    public TerminalControl(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
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
                FlushPendingOutput();
                break;
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
            var session = new TerminalSession();
            session.OutputReceived += OnSessionOutput;
            session.Exited += OnSessionExited;
            session.Start(_workingDirectory, columns, rows);
            _session = session;
        }
        else
        {
            _session.Resize(columns, rows);
        }
    }

    private void OnSessionOutput(byte[] chunk)
    {
        // Raised on the PTY reader thread; the WebView2 lives on the UI thread.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!_rendererReady)
            {
                // Short-lived: the page loads in well under a second.
                if (_pendingOutput.Count < 1024)
                    _pendingOutput.Add(chunk);
                return;
            }
            PostOutput(chunk);
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
        foreach (var chunk in _pendingOutput)
            PostOutput(chunk);
        _pendingOutput.Clear();
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
        _session?.Dispose();
        WebView.Dispose();
    }
}
