param(
    [string]$Executable = "$PSScriptRoot\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class VexNativeSmoke {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    public static IntPtr SendWindowMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam) {
        IntPtr result;
        if (SendMessageTimeoutW(window, message, wparam, lparam, 3, 5000, out result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Vex native smoke: window message failed or timed out.");
        return result;
    }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
function Send-NativeText([IntPtr]$Window, [string]$Text) {
    foreach ($unit in $Text.ToCharArray()) {
        [VexNativeSmoke]::SendWindowMessage($Window, 0x102, [IntPtr][int]$unit, [IntPtr]::Zero) | Out-Null
    }
}
function Get-NativeProbe([IntPtr]$Window, [int]$Query) {
    return [VexNativeSmoke]::SendWindowMessage($Window, 0x8002, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
function Assert-NativeProbe([IntPtr]$Window, [int]$Query, [long]$Expected, [string]$Check) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $actual = Get-NativeProbe $Window $Query
        if ($actual -eq $Expected) { return }
        Start-Sleep -Milliseconds 30
    } until ([DateTime]::UtcNow -gt $deadline)
    throw "Vex native smoke: $Check (query=$Query expected=$Expected actual=$actual)."
}
function Invoke-NativeAction([IntPtr]$Window, [int]$Action, [int]$Index = 0) {
    [VexNativeSmoke]::SendWindowMessage($Window, 0x8005, [IntPtr]$Action, [IntPtr]$Index) | Out-Null
}
function Save-NativeScreenshot([IntPtr]$Window, [string]$Path) {
    [VexNativeSmoke]::SetForegroundWindow($Window) | Out-Null
    Start-Sleep -Milliseconds 300
    $bounds = New-Object VexNativeSmoke+Rect
    if (![VexNativeSmoke]::GetWindowRect($Window, [ref]$bounds)) { throw 'Vex native smoke: screenshot bounds unavailable.' }
    $bitmap = New-Object Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Start-NativeInstance {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -WorkingDirectory $PSScriptRoot -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 20
        $process.Refresh()
        if ($process.HasExited) { throw 'Vex native smoke: process exited before opening its window.' }
    } until ($process.MainWindowHandle -ne [IntPtr]::Zero -or [DateTime]::UtcNow -gt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { $process.Kill(); throw 'Vex native smoke: window startup timed out.' }
    return [pscustomobject]@{ Process = $process; Window = $process.MainWindowHandle; ReadyMs = $watch.Elapsed.TotalMilliseconds }
}
function Close-NativeInstance($Process, [IntPtr]$Window) {
    [VexNativeSmoke]::SendWindowMessage($Window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (!$Process.WaitForExit(5000)) { throw 'Vex native smoke: clean shutdown timed out.' }
    if ($Process.ExitCode -ne 0) { throw "Vex native smoke: unexpected exit code $($Process.ExitCode)." }
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$metrics = Join-Path $OutputDirectory 'startup.txt'
Remove-Item $metrics -ErrorAction SilentlyContinue
$oldMetrics = $env:VEX_NATIVE_METRICS
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
try {
    $previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $env:VEX_NATIVE_METRICS = $metrics
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $windowReadyMs = $instance.ReadyMs
    # Split sentinels so echoed input cannot pass a parsed-viewport assertion.
    Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
    Assert-NativeProbe $window 0 1 'real ConPTY round trip'
    if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 60, 60, 960, 600, 0x0040)) { throw 'Vex native smoke: window resize failed.' }
    Start-Sleep -Milliseconds 250
    Assert-NativeProbe $window 0 1 'resize preserves viewport'
    Send-NativeText $window ('@set VEX_FLOOD=VEX_NATIVE_FLOOD_' + "`r" + '@cls & for /l %i in (1,1,2000) do @echo NATIVE_FLOOD_%i' + "`r" + '@echo %VEX_FLOOD%OK' + "`r")
    Assert-NativeProbe $window 1 1 '2,000-line output settles'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-window.png')
    $process.Refresh()
    $singlePaneMemory = $process.WorkingSet64
    $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 2
    $process.Refresh()
    $idleCpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore

    Send-NativeText $window ('@cls' + "`r" + '@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%A' + "`r")
    Assert-NativeProbe $window 2 1 'first pane marker'
    Invoke-NativeAction $window 1
    Assert-NativeProbe $window 11 2 'split right creates two leaves'
    Assert-NativeProbe $window 13 2 'split owns two independent sessions'
    Assert-NativeProbe $window 2 0 'new pane does not inherit first pane output'
    Send-NativeText $window ('@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%B' + "`r")
    Assert-NativeProbe $window 3 1 'right pane has its own shell'
    [VexNativeSmoke]::SendWindowMessage($window, 0x201, [IntPtr]1, [IntPtr]((150 -shl 16) -bor 100)) | Out-Null
    Assert-NativeProbe $window 2 1 'click focuses first pane'
    Assert-NativeProbe $window 3 0 'first pane does not contain right pane output'
    Invoke-NativeAction $window 2
    Assert-NativeProbe $window 11 3 'nested split down creates a third leaf'
    $closedToken = Get-NativeProbe $window 12
    Send-NativeText $window ('@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%C' + "`r")
    Assert-NativeProbe $window 6 1 'nested pane shell'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-workspace.png')
    $process.Refresh()
    $threePaneMemory = $process.WorkingSet64
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 11 2 'close promotes sibling'
    Assert-NativeProbe $window 2 1 'close focuses the first remaining leaf'
    [VexNativeSmoke]::SendWindowMessage($window, 0x8001, [IntPtr]$closedToken, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 2 1 'stale output token cannot target a surviving pane'
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 11 1 'close collapses root split'
    Assert-NativeProbe $window 3 1 'sibling terminal state survives tree collapse'
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 10 1 'final tab stays open'
    Assert-NativeProbe $window 13 1 'closed pane workers/resources are removed'

    $paneDirectory = Join-Path $OutputDirectory ([string][char]0x017B + [char]0x00F3 + [char]0x0142 + [char]0x0107 + ' pane with spaces')
    New-Item -ItemType Directory -Force $paneDirectory | Out-Null
    Send-NativeText $window ('@cd /d "' + $paneDirectory + '"' + "`r")
    $reportDirectory = '@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "[Console]::Write([char]27 + ' + "']7;' + ([uri](Get-Location).Path).AbsoluteUri + [char]7);[Console]::WriteLine('VEX_NATIVE_DIRECTORY_'+'REPORTED')" + '"'
    Send-NativeText $window ($reportDirectory + "`r")
    Assert-NativeProbe $window 30 1 'shell OSC 7 report crosses the real ConPTY stream'
    Invoke-NativeAction $window 1
    Send-NativeText $window ('@set VEX_DIRECTORY=VEX_NATIVE_DIRECTORY_' + "`r" + '@if /i "%CD%"=="' + $paneDirectory + '" echo %VEX_DIRECTORY%OK' + "`r")
    Assert-NativeProbe $window 8 1 'split inherits a reported Unicode/spaced working directory'
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 13 1 'directory-owning pane resources are freed on close'

    Send-NativeText $window ('@set VEX_TAB=VEX_NATIVE_TAB_' + "`r" + '@echo %VEX_TAB%A' + "`r")
    Assert-NativeProbe $window 4 1 'first tab marker'
    Invoke-NativeAction $window 0
    Assert-NativeProbe $window 10 2 'new tab'
    $freshToken = Get-NativeProbe $window 12
    if ($freshToken -le $closedToken) { throw 'Vex native smoke: a pane token was reused.' }
    Assert-NativeProbe $window 4 0 'new tab output isolation'
    Send-NativeText $window ('@set VEX_ROOT=VEX_NATIVE_ROOT_DIRECTORY_' + "`r" + '@if /i "%CD%"=="' + $PSScriptRoot + '" echo %VEX_ROOT%OK' + "`r")
    Assert-NativeProbe $window 9 1 'new tabs use the project root, not a changed pane directory'
    Send-NativeText $window ('@set VEX_TAB=VEX_NATIVE_TAB_' + "`r" + '@echo %VEX_TAB%B' + "`r")
    Assert-NativeProbe $window 5 1 'second tab marker'
    Invoke-NativeAction $window 5
    Assert-NativeProbe $window 4 1 'previous tab retains its terminal'
    Assert-NativeProbe $window 5 0 'previous tab cannot see second tab output'
    Invoke-NativeAction $window 4
    Assert-NativeProbe $window 5 1 'next tab retains its terminal'
    Send-NativeText $window ('@set VEX_BACKGROUND=VEX_NATIVE_BACKGROUND_' + "`r" + '@ping -n 2 127.0.0.1 >nul & @echo %VEX_BACKGROUND%OK' + "`r")
    Invoke-NativeAction $window 5
    Start-Sleep -Milliseconds 300
    $paintCountBefore = Get-NativeProbe $window 20
    Start-Sleep -Milliseconds 1200
    $backgroundPaints = (Get-NativeProbe $window 20) - $paintCountBefore
    if ($backgroundPaints -ne 0) { throw "Vex native smoke: background output caused $backgroundPaints active-window paints." }
    Assert-NativeProbe $window 7 0 'hidden output does not change the active tab'
    Invoke-NativeAction $window 4
    Assert-NativeProbe $window 7 1 'hidden output is parsed before tab becomes visible'

    $terminalProcessId = Get-NativeProbe $window 14
    Send-NativeText $window ('@ping -n 100 127.0.0.1 >nul' + "`r")
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$terminalProcessId" | Where-Object { $_.Name -eq 'PING.EXE' })
    } until ($children.Count -gt 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($children.Count -eq 0) { throw 'Vex native smoke: process-tree teardown fixture did not start its child.' }
    Invoke-NativeAction $window 7 1
    Assert-NativeProbe $window 10 1 'close active tab selects its left neighbor'
    Assert-NativeProbe $window 4 1 'neighbor terminal survives closing a busy tab'
    foreach ($child in $children) {
        if (Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue) { throw 'Vex native smoke: closed pane left a child process running.' }
    }
    if (Get-Process -Id $terminalProcessId -ErrorAction SilentlyContinue) { throw 'Vex native smoke: closed pane left its shell running.' }
    for ($index = 1; $index -lt 8; $index++) { Invoke-NativeAction $window 0 }
    Invoke-NativeAction $window 0
    Assert-NativeProbe $window 10 8 '8-tab project limit matches WPF'
    Invoke-NativeAction $window 6 999
    Assert-NativeProbe $window 10 8 'invalid tab index is ignored safely'
    for ($index = 7; $index -gt 0; $index--) { Invoke-NativeAction $window 7 $index }
    Assert-NativeProbe $window 13 1 'closing background tabs releases their sessions'
    Close-NativeInstance $process $window
    $entryMilestones = @(Get-Content $metrics | ForEach-Object { [string]$_ })

    $env:VEX_NATIVE_METRICS = $null
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('@for /l %i in (1,1,1000000) do @echo NATIVE_BUSY_%i' + "`r")
    Start-Sleep -Milliseconds 200
    if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1000, 650, 0x0040)) { throw 'Vex native smoke: flooding resize failed.' }
    Invoke-NativeAction $window 0
    Invoke-NativeAction $window 5
    Invoke-NativeAction $window 7 0
    Assert-NativeProbe $window 13 1 'closing a flooding background tab does not affect the survivor'
    Send-NativeText $window ('@for /l %i in (1,1,1000000) do @echo NATIVE_BUSY_%i' + "`r")
    Start-Sleep -Milliseconds 200
    Close-NativeInstance $process $window
    [pscustomobject]@{
        Result = 'PASS'
        Checks = @('ConPTY round trip/resize', '2,000-line output', 'nested right/down splits', 'click focus', 'isolated pane/tab output', 'stale output token rejection', 'background parsing', 'Unicode OSC 7 split directory', 'new-tab project directory', 'close/focus fallback', '8-tab limit', 'process-tree teardown', 'resize under active output', 'flooding-tab close', 'active-output shutdown')
        WindowReadyMs = [Math]::Round($windowReadyMs, 2)
        SinglePaneWorkingSetBytes = $singlePaneMemory
        ThreePaneWorkingSetBytes = $threePaneMemory
        IdleCpuMsOverTwoSeconds = $idleCpuMs
        PaintsForHiddenTabOutput = $backgroundPaints
        EntryMilestones = $entryMilestones
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill() }
    if ($previousDpiContext -and $previousDpiContext -ne [IntPtr]::Zero) { [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null }
    $env:VEX_NATIVE_METRICS = $oldMetrics
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
