param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\output-pacing"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
function Get-RendererMetric([IntPtr]$Window, [int]$Query) {
    return [VexNativeSmoke]::SendWindowMessage($Window, 0x8008, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
function Request-Frame([IntPtr]$Window, [int]$Kind) {
    [VexNativeSmoke]::SendWindowMessage($Window, 0x8009, [IntPtr]$Kind, [IntPtr]::Zero) | Out-Null
}
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
    Assert-NativeProbe $window 0 1 'output pacing shell round trip'
    Start-Sleep -Milliseconds 800
    $refresh = Get-RendererMetric $window 55
    if ($refresh -lt 24 -or $refresh -gt 1000) { throw 'Output pacing: monitor cadence unavailable.' }
    [VexNativeSmoke]::SendWindowMessage($window, 0x7e, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if ((Get-RendererMetric $window 55) -ne $refresh) { throw 'Output pacing: display-change refresh changed unexpectedly.' }
    $before = Get-RendererMetric $window 2
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Request-Frame $window 3
        Start-Sleep -Milliseconds 1
    } while ($watch.ElapsedMilliseconds -lt 600)
    Start-Sleep -Milliseconds 50
    $watch.Stop()
    $frames = (Get-RendererMetric $window 2) - $before
    $limit = [Math]::Ceiling($refresh * $watch.Elapsed.TotalSeconds) + 3
    if ($frames -lt 3 -or $frames -gt $limit) { throw "Output pacing: stream frames=$frames, monitor=$refresh Hz, bound=$limit." }
    # A single deferred request must wake the pump without a later pipe message or metadata tick.
    [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $before = Get-RendererMetric $window 2
    Request-Frame $window 3
    Start-Sleep -Milliseconds 80
    if ((Get-RendererMetric $window 2) -ne $before + 1) { throw 'Output pacing: the last deferred request did not present exactly once.' }
    # An OS paint must consume the pending timer, not cause a second presentation later.
    [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Request-Frame $window 3
    $before = Get-RendererMetric $window 2
    [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 80
    if ((Get-RendererMetric $window 2) -ne $before + 1) { throw 'Output pacing: stale timer duplicated an OS paint.' }
    # A normal UI request overrides a deferred output frame through the ordinary posted-message path.
    Request-Frame $window 3
    Request-Frame $window 0
    Start-Sleep -Milliseconds 80
    $requestUs = Get-RendererMetric $window 27
    if ($requestUs -gt 50000) { throw "Output pacing: ordinary UI request stalled for $requestUs us." }
    $before = Get-RendererMetric $window 2
    $process.Refresh()
    $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Milliseconds 2000
    $process.Refresh()
    $idleCpu = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
    if ((Get-RendererMetric $window 2) -ne $before) { throw 'Output pacing: stopped requests left an idle repaint loop.' }
    $report = [pscustomobject]@{
        Result = 'PASS'; MonitorRefreshHz = $refresh; StreamFrames = $frames
        StreamObservationMs = $watch.Elapsed.TotalMilliseconds; UiRequestToSceneUs = $requestUs
        IdleCpuMsOverTwoSeconds = $idleCpu
        Scope = 'Production output scheduler with synthetic requests; OS paint coalescing, UI priority and idle suppression; not scanout latency.'
    }
    $report | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'output-pacing.json')
    $report | ConvertTo-Json
    Close-NativeInstance $process $window
    $process = $null
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
