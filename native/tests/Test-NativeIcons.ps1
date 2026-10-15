param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function Assert-NativeIconMatch([IntPtr]$Window, [string]$Name) {
    Save-NativeScreenshot $Window (Join-Path $OutputDirectory "icons-$Name.png")
    $tab = [VexNativeSmoke]::SendWindowMessage($Window, 0x8008, [IntPtr]30, [IntPtr]::Zero).ToInt64()
    $pane = [VexNativeSmoke]::SendWindowMessage($Window, 0x8008, [IntPtr]31, [IntPtr]::Zero).ToInt64()
    if ($tab -eq 0 -or $pane -eq 0 -or $tab -ne $pane) {
        throw "Vex icons: $Name tab and pane used different glyph/color/size/badge data (tab=$tab pane=$pane)."
    }
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
    Assert-NativeProbe $window 0 1 'icon fixture real shell round trip'
    Assert-NativeIconMatch $window 'shell-dark'
    Invoke-NativeAction $window 10 7
    Assert-NativeIconMatch $window 'shell-light'
    Invoke-NativeAction $window 10 1
    [VexNativeSmoke]::SendDpiChangedAtCurrentBounds($window)
    Assert-NativeIconMatch $window 'shell-dpi'

    $command = '@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "'
    $command += "[Console]::Write([char]27 + ']0;nvim' + [char]7);Start-Sleep -Seconds 20" + '"'
    Send-NativeText $window ($command + "`r")
    Assert-NativeProbe $window 38 32 'OSC title selects the Neovim icon'
    Assert-NativeIconMatch $window 'application'
    Send-NativeText $window ([string][char]3)

    Invoke-NativeAction $window 0
    Invoke-NativeAction $window 1
    Assert-NativeProbe $window 11 2 'split owns independent shell panes'
    Assert-NativeIconMatch $window 'split-shell'
    Send-NativePointer $window 0x201 70 100
    Send-NativePointer $window 0x202 70 100
    Assert-NativeIconMatch $window 'split-focus'
    Close-NativeInstance $process $window
    $process = $null
    $report = [pscustomobject]@{
        Result = 'PASS'
        Checks = @('actual rendered tab/pane glyph/color/size/badge equality', 'dark/light themes', 'same-monitor synthetic DPI', 'application icon', 'split focus')
        Limitations = @('matching presentation data plus inspected screenshots, not pixel-identical placement across fractional DPI origins')
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'icons.json')
    $report | ConvertTo-Json -Depth 4
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
