param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\status-presentation",
    [switch]$RequireGraphicsDebug
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
function Send-PresentationReport([IntPtr]$Window, [string]$State, [string]$App, [string]$Title, [string]$Message, [switch]$UpdateLater) {
    $title64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Title))
    $message64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Message))
    $body = "state=${State}:app=${App}:title=${title64}:msg=${message64}"
    if ($State -eq 'blocked') { $body += ':kind=permission' }
    if ($State -eq 'working') { $body += ':progress=42' }
    $script = '$e = [char]27; [Console]::Write($e + ''[2J'' + $e + ''[H''); '
    $script += "[Console]::Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$title64')) + [char]13 + [char]10 + [char]13 + [char]10 + [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$message64')) + [char]13 + [char]10); "
    $script += "[Console]::Write(`$e + ']0;$App' + [char]7 + `$e + ']7501;$body' + [char]7)"
    if ($UpdateLater) {
        $updated = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('Reviewing ownership'))
        $script += "; Start-Sleep -Seconds 10; [Console]::Write(`$e + ']7501;state=working:progress=42:app=${App}:title=${title64}:msg=${updated}' + [char]7); Start-Sleep -Seconds 20"
    }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    Send-NativeText $Window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -EncodedCommand ' + $encoded + "`r")
}
function Save-PresentationCapture([IntPtr]$Window, [string]$Name) {
    if ([VexNativeSmoke]::SendWindowMessage($Window, 0x8007, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -ne 1) {
        throw 'Vex status UI: native GPU readback failed.'
    }
    $path = Join-Path $OutputDirectory "$Name.bmp"
    Copy-Item $env:VEX_NATIVE_CAPTURE_BMP $path -Force
    $image = [Drawing.Bitmap]::FromFile($path)
    try { $image.Save((Join-Path $OutputDirectory "$Name.png"), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $image.Dispose() }
    return (Get-FileHash $path).Hash
}
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.status-ui.bmp'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $graphicsDebug = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]9, [IntPtr]::Zero).ToInt64()
    if ($RequireGraphicsDebug -and $graphicsDebug -ne 1) { throw 'Vex status UI: D3D12 validation is required.' }
    [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1280, 780, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 300
    $firstPane = Get-NativeProbe $window 12
    $firstPid = Get-NativeProbe $window 14
    Send-PresentationReport $window 'working' 'Claude' 'Renderer review' 'Reading atlas code' -UpdateLater
    Assert-NativeProbe $window 31 2 'working report arrives through real ConPTY'
    Invoke-NativeAction $window 0
    Send-PresentationReport $window 'idle' 'PowerShell' 'Local shell' 'Idle'
    Assert-NativeProbe $window 31 1 'idle is explicitly reported, not inferred from absent output'
    Save-PresentationCapture $window 'status-before-warmup' | Out-Null
    Start-Sleep -Milliseconds 300
    $before = Save-PresentationCapture $window 'status-horizontal-before'
    $paints = Get-NativeProbe $window 20
    Start-Sleep -Seconds 11
    if ((Get-NativeProbe $window 20) -le $paints) { throw 'Vex status UI: hidden message-only report did not request a paint.' }
    $after = Save-PresentationCapture $window 'status-horizontal-updated'
    if ($before -eq $after) { throw 'Vex status UI: updated background-tab subtitle did not reach GPU pixels.' }
    Assert-NativeProbe $window 47 1 'background status never steals selected-tab focus'
    Invoke-NativeAction $window 6 0
    Assert-NativeProbe $window 12 $firstPane 'returning to the working tab retains pane identity'
    Assert-NativeProbe $window 14 $firstPid 'presentation changes never recreate ConPTY'
    Invoke-NativeAction $window 1
    Send-PresentationReport $window 'blocked' 'Codex' 'Test approval' 'Approve next command'
    Assert-NativeProbe $window 31 4 'permission state remains blocked'
    Invoke-NativeAction $window 2
    Send-PresentationReport $window 'done' 'Cargo' 'Build finished' 'Done'
    Assert-NativeProbe $window 31 3 'finished task uses real done state'
    Invoke-NativeAction $window 1
    Send-PresentationReport $window 'error' 'Git' 'Tests failed' 'Inspect the test log'
    Assert-NativeProbe $window 31 5 'error state is still explicit'
    Start-Sleep -Milliseconds 400
    $iconCount = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]32, [IntPtr]::Zero).ToInt64()
    if ($iconCount -lt 4) { throw "Vex status UI: expected four stacked process favicons and +N overflow, got $iconCount." }
    Save-PresentationCapture $window 'status-dark-stacks' | Out-Null
    Invoke-NativeAction $window 10 7
    Save-PresentationCapture $window 'status-light-stacks' | Out-Null
    Invoke-NativeAction $window 10 1
    Invoke-NativeAction $window 8 0
    Send-NativeText $window 'Pinned task'
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]13, [IntPtr]::Zero) | Out-Null
    Send-PresentationReport $window 'error' 'Git' 'New reported task' 'Still failed'
    Assert-NativeProbe $window 31 5 'new metadata arrives after committing a custom tab title'
    Save-PresentationCapture $window 'status-custom-title' | Out-Null
    Assert-NativeProbe $window 10 2 'the existing horizontal tab model keeps its two tabs'
    [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 800, 500, 0x0040) | Out-Null
    Save-PresentationCapture $window 'status-compact' | Out-Null
    $errors = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]6, [IntPtr]::Zero).ToInt64()
    if ($errors -ne 0) { throw "Vex status UI: D3D12 validation reported $errors errors." }
    Close-NativeInstance $process $window
    $process = $null
    $report = [pscustomobject]@{
        Result = 'PASS'; GraphicsDebug = [bool]$graphicsDebug
        Checks = @('horizontal title/subtitle presentation', 'real working/idle/blocked/done/error reports', 'message-only hidden-tab repaint without focus theft', 'stable ConPTY identity', 'four stacked favicons and overflow', 'custom title presentation', 'dark/light and compact native GPU captures')
        Limitations = @('Rex-inspired presentation, not pixel-identical macOS UI', 'no separate activity bar, vertical tabs or remote sessions', 'synthetic keyboard/window messages; physical DPI and accessibility remain unverified')
    }
    $report | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutputDirectory 'status-presentation.json')
    $report | ConvertTo-Json -Depth 3
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
