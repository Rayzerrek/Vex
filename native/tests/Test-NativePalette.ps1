param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\palette"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\ModalHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$previousSelftest = $env:VEX_NATIVE_SELFTEST
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $pidBefore = Get-NativeProbe $window 14

    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window 'Command Palette'
    $count = Get-NativeProbe $window 101
    if ($count -lt 20) { throw "Vex palette: expected the full command list, got $count commands." }
    Post-DialogKey $window 0x26
    Assert-NativeProbe $window 100 ($count - 1) 'Up wraps from the first command to the last, beyond the first nine rows'
    Post-DialogKey $window 0x24
    Assert-NativeProbe $window 100 0 'Home returns to the first command'
    Post-DialogKey $window 0x23
    Assert-NativeProbe $window 100 ($count - 1) 'End reaches the last command'
    Post-DialogKey $window 0x0D
    Assert-NativeProbe $window 53 7 'Enter on the last command (Settings) opens the native settings dialog'
    Assert-NativeProbe $window 14 $pidBefore 'palette commands do not recreate the PTY'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Escape closes the settings dialog'

    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window 'Command Palette'
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), 'splr') | Out-Null
    Assert-NativeProbe $window 103 4 'fuzzy query text reaches the terminal owner'
    if ((Get-NativeProbe $window 101) -eq 0) { throw 'Vex palette: fuzzy query has no matches.' }
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-fuzzy.png')
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), ('x' * 126) + [char]::ConvertFromUtf32(0x1F600)) | Out-Null
    Assert-NativeProbe $window 103 126 'query limit keeps complete UTF-8 characters'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Escape closes the palette'

    Invoke-NativeAction $window 10 7
    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window 'Command Palette'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-light.png')
    [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 640, 400, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 100
    $dialog = Get-OwnedModal $window 'Command Palette'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-compact.png')
    Post-DialogKey $window 0x1B
    Close-NativeInstance $process $window
    $process = $null
    [pscustomobject]@{ Result = 'PASS'; Checks = @('every action reachable with keyboard', 'Up/Home/End wrap and scroll', 'native fuzzy query', 'UTF-8 query limit', 'stable PTY', 'dark/light/compact native captures') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $previousSelftest
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
}
