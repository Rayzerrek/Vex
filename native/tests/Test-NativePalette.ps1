param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\palette"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$previousSelftest = $env:VEX_NATIVE_SELFTEST
$previousCapture = $env:VEX_NATIVE_CAPTURE_BMP
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.capture.bmp'
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $pidBefore = Get-NativeProbe $window 14
    $blink = Get-NativeProbe $window 72
    Invoke-NativeAction $window 9
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x26, [IntPtr]::Zero) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x0D, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 72 (1 - $blink) 'Up wraps to the last action, beyond the first nine commands'
    Assert-NativeProbe $window 14 $pidBefore 'palette action does not recreate the PTY'
    Invoke-NativeAction $window 17
    Invoke-NativeAction $window 9
    Send-NativeText $window 'splr'
    if ((Get-NativeProbe $window 101) -eq 0) { throw 'Vex palette: fuzzy query has no matches.' }
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'palette-fuzzy.png')
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x1B, [IntPtr]::Zero) | Out-Null
    Invoke-NativeAction $window 9
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x23, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 100 19 'End reaches the last command'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'palette-last-page.png')
    if ((Get-NativeProbe $window 102) -eq 0) { throw 'Vex palette: End did not scroll the viewport.' }
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x24, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 100 0 'Home returns to the first command'
    Send-NativeText $window ((('x' * 127) -join '') + [char]::ConvertFromUtf32(0x1F600))
    Assert-NativeProbe $window 103 127 'query limit never splits an emoji'
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x1B, [IntPtr]::Zero) | Out-Null
    Invoke-NativeAction $window 10 7
    Invoke-NativeAction $window 9
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'palette-light.png')
    [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 640, 400, 0x0040) | Out-Null
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'palette-compact.png')
    Close-NativeInstance $process $window
    $process = $null
    [pscustomobject]@{ Result = 'PASS'; Checks = @('every action reachable', 'fuzzy search', 'Home/End and viewport scrolling', 'UTF-8 query limit', 'stable PTY', 'dark/light/compact captures') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $previousSelftest
    $env:VEX_NATIVE_CAPTURE_BMP = $previousCapture
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
}
