using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Vex.Terminal.Native;
using Microsoft.Win32.SafeHandles;

namespace Vex.Terminal;

/// <summary>
/// One long-lived terminal process attached to a Windows Pseudo Console,
/// rendered elsewhere — the analogue of upstream's <c>TerminalSession</c>.
/// The raw VT byte stream is exposed through <see cref="OutputReceived"/>;
/// turning it into pixels is the view's business.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private IntPtr _pseudoConsole;
    private FileStream? _ptyOutput;
    private FileStream? _ptyInput;
    private IntPtr _processHandle;
    private IntPtr _threadHandle;
    private RegisteredWaitHandle? _exitWaitHandle;
    private int _exitedRaised;
    private bool _disposed;

    // Input-pipe writes come from two threads now: the UI thread (keystrokes,
    // mouse reports, pastes) and the PTY reader thread (synchronous terminal
    // query responses such as DSR). FileStream writes must not interleave.
    private readonly object _writeLock = new();

    /// <summary>Raw bytes produced by the child process, as read from the PTY.</summary>
    public event Action<ArraySegment<byte>>? OutputReceived;

    /// <summary>Raised once, with the process exit code, when the child exits.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    /// <summary>
    /// The closest Windows analogue of upstream's "login shell": PowerShell 7
    /// when installed, Windows PowerShell otherwise.
    /// </summary>
    public static string DefaultShell()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var cmd = Path.Combine(system32, "cmd.exe");
        if (File.Exists(cmd)) return cmd;
        return "cmd.exe";
    }

    public static string PowerShell()
    {
        var pwsh = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh))
            return pwsh;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public void Start(string workingDirectory, short columns, short rows, string? shell = null, string? arguments = null)
    {
        if (_pseudoConsole != IntPtr.Zero)
            throw new InvalidOperationException("Session is already started.");

        _columns = columns;
        _rows = rows;

        var sa = new NativeMethods.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<NativeMethods.SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };

        // Our end of each pipe stays with us; the other end goes to ConPTY.
        if (!NativeMethods.CreatePipe(out var inputPtySide, out var inputOurSide, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!NativeMethods.CreatePipe(out var outputOurSide, out var outputPtySide, ref sa, 0))
        {
            NativeMethods.CloseHandle(inputPtySide);
            NativeMethods.CloseHandle(inputOurSide);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // Ensure our side of the pipes is NOT inherited by child processes.
        NativeMethods.SetHandleInformation(inputOurSide, NativeMethods.HANDLE_FLAG_INHERIT, 0);
        NativeMethods.SetHandleInformation(outputOurSide, NativeMethods.HANDLE_FLAG_INHERIT, 0);

        var size = new NativeMethods.COORD(columns, rows);
        var hr = NativeMethods.CreatePseudoConsole(size, inputPtySide, outputPtySide, 0, out _pseudoConsole);

        // The pseudoconsole owns its pipe ends now, success or failure.
        NativeMethods.CloseHandle(inputPtySide);
        NativeMethods.CloseHandle(outputPtySide);

        if (hr != 0)
        {
            NativeMethods.CloseHandle(inputOurSide);
            NativeMethods.CloseHandle(outputOurSide);
            throw new Win32Exception(hr);
        }

        _ptyInput = new FileStream(new SafeFileHandle(inputOurSide, ownsHandle: true), FileAccess.Write, BufferSize, isAsync: false);
        _ptyOutput = new FileStream(new SafeFileHandle(outputOurSide, ownsHandle: true), FileAccess.Read, BufferSize, isAsync: false);

        var shellExe = shell is null ? DefaultShell() : shell;
        var commandLine = shellExe.StartsWith('"') ? shellExe : $"\"{shellExe}\"";
        if (!string.IsNullOrEmpty(arguments))
            commandLine += $" {arguments}";

        SpawnChild(commandLine, workingDirectory);

        StartReaderLoop();
        StartExitWaiter();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            lock (_writeLock)
            {
                if (_ptyInput is null || _disposed)
                    return;
                _ptyInput.Write(data);
                _ptyInput.Flush();
            }
        }
        catch (IOException)
        {
            // The child went away between the liveness check and the write.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private short _columns;
    private short _rows;

    public void Resize(short columns, short rows)
    {
        if (columns == _columns && rows == _rows) return;
        _columns = columns;
        _rows = rows;

        if (_pseudoConsole != IntPtr.Zero && !_disposed)
        {
            var hr = NativeMethods.ResizePseudoConsole(_pseudoConsole, new NativeMethods.COORD(columns, rows));
            if (hr != 0)
            {
                // Resize failures leave ConPTY at its old geometry while Vex
                // reports the new grid; this desyncs the cursor/scroll region
                // and can cause text to paint at wrong rows (visual duplication).
                // Keep the managed dimensions authoritative but surface the
                // failure so the UI can flag the pane.
                System.Diagnostics.Debug.WriteLine($"ConPTY resize failed: HRESULT=0x{hr:X8} {columns}x{rows}");
            }
        }
    }

    private void SpawnChild(string commandLine, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(workingDirectory))
                workingDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        var attributeListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>(),
                    // STARTF_USESTDHANDLES stops the child from inheriting or attaching to the parent's console window.
                    dwFlags = NativeMethods.STARTF_USESTDHANDLES,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = IntPtr.Zero,
                    hStdError = IntPtr.Zero,
                },
                lpAttributeList = attributeList,
            };

            // CreateProcess may write into the command line buffer; it must be mutable.
            var mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
            if (!NativeMethods.CreateProcessW(
                    null,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            _processHandle = processInfo.hProcess;
            _threadHandle = processInfo.hThread;
            ProcessId = processInfo.dwProcessId;
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private void StartReaderLoop()
    {
        // Keep the stream for the lifetime of the reader. Dispose closes this
        // instance to interrupt Read; it also clears _ptyOutput, which must
        // not turn a close racing with the next loop iteration into a
        // NullReferenceException on this background thread.
        var output = _ptyOutput ?? throw new InvalidOperationException("PTY output is not initialized.");
        var reader = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
                    var read = output.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                        break;
                    }
                    var handler = OutputReceived;
                    if (handler != null)
                        handler.Invoke(new ArraySegment<byte>(buffer, 0, read));
                    else
                        System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (IOException)
            {
                // Broken pipe: the pseudoconsole went down with the child.
            }
            catch (ObjectDisposedException)
            {
            }
            RaiseExitedOnce();
        })
        {
            IsBackground = true,
            Name = "Vex-pty-reader",
        };
        reader.Start();
    }

    private void StartExitWaiter()
    {
        // Use a thread-pool registered wait instead of a dedicated OS thread
        // per terminal pane. The registered wait uses the thread pool's
        // I/O completion port infrastructure, so dozens of panes do not each
        // reserve a full thread stack just to block in WaitForSingleObject.
        var waitHandle = new ProcessWaitHandle(_processHandle);
        _exitWaitHandle = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, _) => RaiseExitedOnce(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    /// <summary>Wraps a raw process handle in a WaitHandle so it can be used
    /// with ThreadPool.RegisterWaitForSingleObject. Does not own the handle;
    /// TerminalSession.Dispose closes it.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false);
        }
    }

    private void RaiseExitedOnce()
    {
        if (Interlocked.CompareExchange(ref _exitedRaised, 1, 0) != 0)
            return;

        var exitCode = -1;
        if (_processHandle != IntPtr.Zero &&
            NativeMethods.GetExitCodeProcess(_processHandle, out var code))
            exitCode = unchecked((int)code);
        Exited?.Invoke(exitCode);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Unregister the wait before closing the handle to avoid a race
        // where the callback fires after disposal.
        _exitWaitHandle?.Unregister(null);
        _exitWaitHandle = null;

        if (_processHandle != IntPtr.Zero)
            NativeMethods.TerminateProcess(_processHandle, 1);
        if (_pseudoConsole != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        // Serialize with concurrent Write calls from the UI and PTY threads
        // so a query response cannot land on a half-closed pipe.
        lock (_writeLock)
        {
            try { _ptyInput?.Dispose(); } catch { }
            _ptyInput = null;
        }
        try { _ptyOutput?.Dispose(); } catch { }
        _ptyOutput = null;

        if (_processHandle != IntPtr.Zero)
            NativeMethods.CloseHandle(_processHandle);
        if (_threadHandle != IntPtr.Zero)
            NativeMethods.CloseHandle(_threadHandle);
        _processHandle = IntPtr.Zero;
        _threadHandle = IntPtr.Zero;
    }
}
