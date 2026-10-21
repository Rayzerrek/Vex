param(
    [string]$Executable = (Join-Path $PSScriptRoot '../zig-out/bin/vex-native.exe'),
    [ValidateSet('cmd', 'wsl')][string]$Shell = 'cmd',
    [string]$Distribution = '',
    [ValidateRange(1, 200)][int]$Cycles = 30,
    [ValidateRange(1, 7)][int]$BatchSize = 5,
    [switch]$Warp
)
$ErrorActionPreference = 'Stop'
# Run on an interactive Windows desktop. Never reuse a user's configuration or clipboard.
if ($Distribution -match '["\r\n]') { throw 'Invalid distribution name' }
$Executable = (Resolve-Path $Executable).Path
if (-not ('VexRecoveryNative' -as [type])) {
    Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class VexRecoveryNative {
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetProcessHandleCount(IntPtr process, out uint count);
    public static uint HandleCount(IntPtr process) {
        uint count;
        if (!GetProcessHandleCount(process, out count)) throw new System.ComponentModel.Win32Exception();
        return count;
    }
    [DllImport("user32.dll", SetLastError=true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr w, IntPtr l,
        uint flags, uint timeout, out UIntPtr result);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int length);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    public static uint DialogProcess(long hwnd) {
        uint pid; GetWindowThreadProcessId(new IntPtr(hwnd), out pid); return pid;
    }
    public static long[] WslErrorWindows() {
        var handles = new List<long>();
        EnumWindows((hwnd, data) => {
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            var kind = new StringBuilder(128);
            GetClassName(hwnd, kind, kind.Capacity);
            // Hidden launcher/ConPTY console windows also carry a wsl.exe title.
            if (IsWindowVisible(hwnd) && kind.ToString() == "#32770" &&
                title.ToString().IndexOf("wsl.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                handles.Add(hwnd.ToInt64());
            return true;
        }, IntPtr.Zero);
        return handles.ToArray();
    }
}
'@
}
function Send-Vex([uint32]$Message, [uint64]$W = 0, [long]$L = 0) {
    $script:process.Refresh()
    if ($script:process.HasExited) { throw "Vex exited unexpectedly: $($script:process.ExitCode)" }
    [UIntPtr]$result = [UIntPtr]::Zero
    $sent = [VexRecoveryNative]::SendMessageTimeout($script:window, $Message, [UIntPtr]$W, [IntPtr]$L, 2, 15000, [ref]$result)
    if ($sent -eq [IntPtr]::Zero) { throw "Window did not respond (message $Message, w=$W, l=$L, cycle=$cycle)" }
    return $result.ToUInt64()
}
function Probe([uint32]$Query) { Send-Vex 0x8002 $Query }
function Assert-NoWslErrorDialog {
    $newDialogs = @([VexRecoveryNative]::WslErrorWindows() | Where-Object { $_ -notin $initialWslDialogs })
    if ($newDialogs.Count) {
        $owners = @($newDialogs | ForEach-Object { [VexRecoveryNative]::DialogProcess($_) })
        $details = Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -in $owners } |
            Select-Object ProcessId, ParentProcessId, CommandLine | Format-List | Out-String
        throw "A new WSL application-error dialog appeared during cycle $cycle; it was not dismissed.`n$details"
    }
}
function Action([uint32]$Kind, [int]$Index = 0) {
    Assert-NoWslErrorDialog
    $null = Send-Vex 0x8005 $Kind $Index
    Assert-NoWslErrorDialog
}
function Open-VexTab {
    $before = Probe 10
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Action 0
        if ((Probe 10) -eq ($before + 1)) { return }
        if ((Probe 134) -ne 170) { throw "Tab creation failed (launch $(Probe 134), exit $(Probe 135))" }
        # Bounded native cleanup may reject a burst; the window must remain responsive.
        if ([DateTime]::UtcNow -gt $deadline) { throw "Cleanup backlog did not drain: $(Probe 136)" }
        Start-Sleep -Milliseconds 50
    } while ($true)
}
function Wait-NativeCleanup([int]$Seconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ((Probe 136) -gt 0) {
        Assert-NoWslErrorDialog
        if ([DateTime]::UtcNow -gt $deadline) { throw "Native cleanup is stuck: $(Probe 136)" }
        Start-Sleep -Milliseconds 50
    }
}
function Settle-Renderer {
    # Cursor blink is disabled. Explicitly cycle the buffers so deferred GPU work/cleanup
    # reaches the same quiescent state before both handle snapshots, including WARP workers.
    for ($frame = 0; $frame -lt 6; $frame++) {
        $null = Send-Vex 0x8009 2
        Start-Sleep -Milliseconds 100
    }
}
function Assert-Equal($Expected, $Actual, [string]$Context) {
    if ($Expected -ne $Actual) { throw "${Context}: expected $Expected, got $Actual" }
}
$root = Join-Path ([IO.Path]::GetTempPath()) ('vex-recovery-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
$config = Join-Path $root 'init.lua'
[IO.File]::WriteAllText($config, "vex.config.confirm_close = false`nvex.config.cursor_blink = false`n")
$names = @('VEX_NATIVE_SELFTEST', 'VEX_NATIVE_SETTINGS_PATH', 'VEX_NATIVE_WARP')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$initialWslDialogs = @([VexRecoveryNative]::WslErrorWindows())
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_SETTINGS_PATH = $config
    $env:VEX_NATIVE_WARP = if ($Warp) { '1' } else { '0' }
    $arguments = "--shell $Shell"
    if ($Distribution) { $arguments += " --distribution `"$Distribution`"" }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -WorkingDirectory (Split-Path $Executable) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "Startup failed: $($process.ExitCode)" }
        $window = $process.MainWindowHandle
    } until ($window -ne [IntPtr]::Zero -or [DateTime]::UtcNow -gt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'No application window' }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Probe 13) -ne 1) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'No initial session' }
        Start-Sleep -Milliseconds 100
    }
    $originalToken = Probe 12
    # Warm the same concurrency/geometry as the measured run, including reapers and driver caches.
    for ($warmup = 0; $warmup -lt 3; $warmup++) {
        for ($tab = 0; $tab -lt $BatchSize; $tab++) { Open-VexTab }
        Start-Sleep -Milliseconds 1600
        if ($warmup -eq 2) { $null = Send-Vex 0x8003 (Probe 12) 29 }
        # Render the maximum scene/toast into every swap-chain buffer before comparing handles.
        for ($frame = 0; $frame -lt 3; $frame++) {
            $null = Send-Vex 0x8009 2
            Start-Sleep -Milliseconds 50
        }
        for ($tab = $BatchSize; $tab -gt 0; $tab--) { Action 7 $tab }
        Wait-NativeCleanup
    }
    Settle-Renderer
    $process.Refresh()
    $baselineHandles = [VexRecoveryNative]::HandleCount($process.Handle)
    $handleSamples = @("baseline=$baselineHandles threads=$($process.Threads.Count)")
    for ($cycle = 0; $cycle -lt $Cycles; $cycle++) {
        for ($tab = 0; $tab -lt $BatchSize; $tab++) { Open-VexTab }
        Assert-Equal ($BatchSize + 1) (Probe 10) "Opened tab count (cycle $cycle; launch code $(Probe 134), exit code $(Probe 135))"
        # Alternate rapid teardown with enough time to exercise advisory WSL probes.
        if (($cycle % 3) -eq 0) { Start-Sleep -Milliseconds 1600 }
        for ($tab = $BatchSize; $tab -gt 0; $tab--) { Action 7 $tab }
        Assert-Equal 1 (Probe 10) 'Remaining tab count'
        Assert-Equal $originalToken (Probe 12) 'Original session identity'
        Assert-Equal 0 (Probe 70) 'Original session remains running'
        Assert-NoWslErrorDialog
        if (($cycle % 10) -eq 9) {
            $process.Refresh()
            $handleSamples += "cycle=$cycle handles=$([VexRecoveryNative]::HandleCount($process.Handle)) threads=$($process.Threads.Count) closing=$(Probe 136)"
        }
    }
    Wait-NativeCleanup
    Settle-Renderer
    $process.Refresh()
    $handles = [VexRecoveryNative]::HandleCount($process.Handle)
    # Reapers/thread-pool workers and driver caches may initialize lazily; unbounded growth must not.
    if ($handles -gt ($baselineHandles + 16)) { throw "Possible handle leak: $baselineHandles -> $handles (threads $($process.Threads.Count), forced closes $(Probe 137)); $($handleSamples -join '; ')" }
    $forced = Probe 137
    Open-VexTab
    $failedToken = Probe 12
    $null = Send-Vex 0x8003 $failedToken 29
    Assert-Equal 2 (Probe 10) 'Failed pane retained'
    Assert-Equal 1 (Probe 70) 'Failed transport stopped'
    Assert-Equal 29 (Probe 71) 'Failure code retained'
    Assert-Equal 1 (Send-Vex 0x8008 65) 'Error displayed in toast'
    # Duplicate/stale failure notifications must not close another session.
    $null = Send-Vex 0x8003 $failedToken 29
    Action 7 1
    $null = Send-Vex 0x8003 $failedToken 29
    Assert-Equal $originalToken (Probe 12) 'Healthy pane survives late errors'
    Assert-Equal 0 (Probe 70) 'Healthy transport remains running'
    Wait-NativeCleanup
    Open-VexTab
    Assert-Equal 1 (Send-Vex 0x8008 66) 'Arm delayed native close'
    $forcedBefore = Probe 137
    $closeWatch = [Diagnostics.Stopwatch]::StartNew()
    Action 7 1
    $closeWatch.Stop()
    if ($closeWatch.ElapsedMilliseconds -ge 1000) { throw 'Slow native teardown blocked the window thread' }
    Assert-Equal $originalToken (Probe 12) 'UI remains responsive during slow teardown'
    Wait-NativeCleanup 10
    Assert-Equal ($forcedBefore + 1) (Probe 137) 'Watchdog forced exactly one delayed close'
    $generation = Send-Vex 0x8008 3
    $null = Send-Vex 0x8008 7
    if ((Send-Vex 0x8008 3) -le $generation) { throw 'GPU recovery did not recreate the renderer' }
    Assert-Equal $originalToken (Probe 12) 'GPU recovery retains session'
    $null = Send-Vex 0x10
    if (-not $process.WaitForExit(15000)) { throw 'Application did not shut down' }
    Assert-Equal 0 $process.ExitCode 'Clean shutdown'
    Write-Host "PASS: $Shell, $Cycles cycles x $BatchSize tabs; bounded cleanup/watchdog, transport isolation, toast and GPU recovery; handles $baselineHandles -> $handles, normal forced closes $forced"
} finally {
    if ($process) {
        $process.Refresh()
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Remove-Item -Recurse -Force $root
}
