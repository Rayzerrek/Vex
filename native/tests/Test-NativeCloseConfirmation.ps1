param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\close-confirmation"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Invoke-NativeAction $window 0
    for ($index = 0; $index -lt 17; $index++) { Invoke-NativeAction $window 1 }
    Assert-NativeProbe $window 11 18 'tab contains eighteen real terminal panes'
    Send-NativeStatus $window 'state=working:app=future-tab-close'
    Assert-NativeProbe $window 31 2 'only the final pane reports active unfamiliar work'
    Invoke-NativeAction $window 16 1
    Assert-NativeProbe $window 53 5 'a protected pane beyond sixteen opens close confirmation'
    Assert-NativeProbe $window 13 19 'the close request preserves every live session'
    Invoke-NativeAction $window 16 98
    Assert-NativeProbe $window 53 0 'cancelling preserves the full tab'
    Assert-NativeProbe $window 10 2 'cancel does not remove the protected tab'
    Invoke-NativeAction $window 16 1
    Assert-NativeProbe $window 53 5 'the unchanged tab remains protected'
    Invoke-NativeAction $window 16 97
    Assert-NativeProbe $window 10 1 'confirmation closes the entire protected tab'
    Assert-NativeProbe $window 13 1 'confirmation releases all eighteen sessions'
    Close-NativeInstance $process $window
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    @{ Result = 'PASS'; Checks = @('18 real panes', 'unfamiliar active report beyond sixteen', 'cancel preserves sessions', 'confirm closes every pane') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'close-confirmation.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
