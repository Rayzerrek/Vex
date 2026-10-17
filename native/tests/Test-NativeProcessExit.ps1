param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path (Resolve-Path $OutputDirectory).Path '.capture.bmp'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
    Assert-NativeProbe $window 0 1 'first shell is live'
    $firstPane = Get-NativeProbe $window 12
    $firstPid = Get-NativeProbe $window 14

    Invoke-NativeAction $window 0
    Send-NativeText $window ("exit 0`r")
    Assert-NativeProbe $window 10 1 'shell exit removes a nonfinal tab automatically'
    Assert-NativeProbe $window 14 $firstPid 'tab exit selects the surviving shell'

    Invoke-NativeAction $window 1
    Invoke-NativeAction $window 2
    Send-NativeText $window ("exit 5`r")
    Assert-NativeProbe $window 11 2 'nested exiting split collapses its parent'
    Assert-NativeProbe $window 12 $firstPane 'active split exit selects the first remaining leaf'
    $surface = Get-NativeSurface $window
    Send-NativePointer $window 0x201 ($surface.Width * 0.75) 100
    Send-NativePointer $window 0x202 ($surface.Width * 0.75) 100
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 11 1 'manual close still collapses the surviving split'
    Assert-NativeProbe $window 12 $firstPane 'manual close preserves the first pane'

    Invoke-NativeAction $window 0
    Send-NativeText $window ("@ping -n 3 127.0.0.1 >nul & exit 3`r")
    Invoke-NativeAction $window 5
    Assert-NativeProbe $window 10 1 'background tab exits without selecting itself'
    Assert-NativeProbe $window 12 $firstPane 'background tab exit preserves foreground focus'

    Invoke-NativeAction $window 0
    $hiddenPane = Get-NativeProbe $window 12
    Invoke-NativeAction $window 1
    Send-NativeText $window ("@ping -n 3 127.0.0.1 >nul & exit 4`r")
    Invoke-NativeAction $window 5
    Assert-NativeProbe $window 13 2 'background split releases only its own session'
    Assert-NativeProbe $window 12 $firstPane 'background split exit leaves visible focus unchanged'
    Invoke-NativeAction $window 4
    Assert-NativeProbe $window 11 1 'background split tree was collapsed'
    Assert-NativeProbe $window 12 $hiddenPane 'background split exit focuses its surviving leaf'
    Invoke-NativeAction $window 7 1

    Invoke-NativeAction $window 0
    Invoke-NativeAction $window 6 0
    Invoke-NativeAction $window 1
    Send-NativeText $window ("@ping -n 3 127.0.0.1 >nul & exit 6`r")
    Send-NativePointer $window 0x201 180 20
    Send-NativePointer $window 0x200 220 20 1
    Assert-NativeProbe $window 42 1 'tab drag owns real capture before process exit'
    Assert-NativeProbe $window 11 1 'process exit closes a split during a captured gesture'
    Assert-NativeProbe $window 42 0 'process exit cancels pointer capture before tree edits'
    Invoke-NativeAction $window 7 1
    Assert-NativeProbe $window 13 1 'all removed sessions were released'

    Invoke-NativeAction $window 0
    Send-NativeText $window ("@ping -n 6 127.0.0.1 >nul & exit 8`r")
    Invoke-NativeAction $window 0
    Send-NativeStatus $window 'state=working:app=build'
    Assert-NativeProbe $window 31 2 'target tab has a running-program close confirmation'
    Invoke-NativeAction $window 0
    $neighborPid = Get-NativeProbe $window 14
    Invoke-NativeAction $window 6 2
    Invoke-NativeAction $window 16 2
    Assert-NativeProbe $window 53 5 'close confirmation is open for the intended tab'
    Assert-NativeProbe $window 10 3 'background exit shifts tab indexes while confirmation is open'
    Assert-NativeProbe $window 53 5 'unrelated exit preserves the open confirmation'
    Invoke-NativeAction $window 16 97
    Assert-NativeProbe $window 10 2 'confirmation closes the original tab by stable identity'
    Assert-NativeProbe $window 12 $firstPane 'confirmation uses the current left-neighbor fallback'
    Invoke-NativeAction $window 6 1
    Assert-NativeProbe $window 14 $neighborPid 'index shift must not close the next tab instead'
    Invoke-NativeAction $window 7 1

    Send-NativeText $window ('@set VEX_FINAL=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_FINAL%OK & exit 7' + "`r")
    Assert-NativeProbe $window 70 1 'last tab retains an exited terminal'
    Assert-NativeProbe $window 71 7 'last terminal preserves the real nonzero exit code'
    Assert-NativeProbe $window 10 1 'workspace never loses its final tab'
    Assert-NativeProbe $window 0 1 'final output remains readable'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'process-exit-final.png')
    Invoke-NativeAction $window 0
    Assert-NativeProbe $window 70 0 'new terminal still starts after the last shell exits'
    if ((Get-NativeProbe $window 12) -le $firstPane) { throw 'Vex exit: removed pane identities were reused.' }
    Invoke-NativeAction $window 7 0
    Assert-NativeProbe $window 13 1 'retained exited session can subsequently be closed'
    Send-NativeText $window ("exit 0`r")
    Assert-NativeProbe $window 70 1 'successful final session is retained'
    Assert-NativeProbe $window 71 0 'successful exit card uses the real zero exit code'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'process-exit-success.png')
    Close-NativeInstance $process $window
    $process = $null
    $report = [pscustomobject]@{
        Result = 'PASS'
        Checks = @('automatic active tab/split close', 'nested first-leaf fallback', 'hidden tab/split exit without focus theft', 'capture cancellation', 'stable close-confirmation target after background exit', 'session release', 'last-tab retention/exit code/final output', 'new shell after exit')
        Limitations = @('synthetic pointer gesture requires a quiet desktop', 'not physical keyboard/multi-monitor testing')
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'process-exit.json')
    $report | ConvertTo-Json -Depth 4
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
