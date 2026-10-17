param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\hover-development\bin\vex-native.exe",
    [Parameter(Mandatory = $true)][string]$BaselineExecutable,
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\frame-scheduling",
    [ValidateRange(30,1000)][int]$Frames = 120,
    [ValidateRange(1,10)][int]$Trials = 3,
    [ValidateRange(0,1000)][int]$FrameSpacingMs = 50
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
function Get-RendererMetric([IntPtr]$Window, [int]$Query) {
    return [VexNativeSmoke]::SendWindowMessage($Window, 0x8008, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
function Get-TimingSummary($Values) {
    $sorted = @($Values | Sort-Object)
    return @{ MedianMs = $sorted[[int][Math]::Floor($sorted.Count / 2)]; P95Ms = $sorted[[int][Math]::Ceiling($sorted.Count * 0.95) - 1]; MaxMs = $sorted[-1] }
}
try {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path $OutputDirectory).Path
    $settingsPath = Join-Path $OutputDirectory 'settings.json'
    [IO.File]::WriteAllText($settingsPath, '{"theme":"Vex Obsidian","cursor_blink":false}')
    $env:VEX_NATIVE_SELFTEST = '1'
    $samples = @()
    for ($trial = 0; $trial -lt $Trials; $trial++) {
        $versions = if ($trial % 2 -eq 0) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        foreach ($version in $versions) {
            $path = if ($version -eq 'baseline') { $BaselineExecutable } else { $Executable }
            $instance = Start-NativeInstance -ExecutablePath $path -SettingsPath $settingsPath -BlinkingCursor $true
            $process = $instance.Process; $window = $instance.Window
            [VexNativeSmoke]::SetForegroundWindow($window) | Out-Null
            [VexNativeSmoke]::MoveCursorClient($window, 10, 50)
            Start-Sleep -Milliseconds 1500
            Assert-NativeProbe $window 72 0 'frame timing fixture disables cursor animation through settings'
            if ($version -eq 'candidate') {
                $coalescedBefore = Get-NativeProbe $window 20
                [VexNativeSmoke]::SendWindowMessage($window, 0x8009, [IntPtr]2, [IntPtr]::Zero) | Out-Null
                Start-Sleep -Milliseconds 100
                if ((Get-NativeProbe $window 20) - $coalescedBefore -ne 1) { throw 'Vex frame scheduling: an OS paint failed to consume the queued frame.' }
            }
            $requests = @(); $scenes = @()
            for ($i = 0; $i -lt $Frames + 10; $i++) {
                if ($FrameSpacingMs -gt 0) { Start-Sleep -Milliseconds $FrameSpacingMs }
                $before = Get-NativeProbe $window 20
                $accepted = [VexNativeSmoke]::SendWindowMessage($window, 0x8009, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
                if ($accepted -ne 1) { throw 'Vex frame scheduling: executable lacks the gated frame-timing probe.' }
                $deadline = [DateTime]::UtcNow.AddSeconds(3)
                while ((Get-NativeProbe $window 20) -eq $before) {
                    if ([DateTime]::UtcNow -gt $deadline) { throw 'Vex frame scheduling: requested frame did not render.' }
                    Start-Sleep -Milliseconds 1
                }
                if ($i -ge 10) {
                    $requests += (Get-RendererMetric $window 27) / 1000.0
                    $scenes += (Get-RendererMetric $window 28) / 1000.0
                }
            }
            $synchronizedPaints = $null
            if ($version -eq 'candidate') {
                $syncBefore = Get-NativeProbe $window 20
                $syncWatch = [Diagnostics.Stopwatch]::StartNew()
                while ($syncWatch.ElapsedMilliseconds -lt 600) {
                    [VexNativeSmoke]::SendWindowMessage($window, 0x8009, [IntPtr]1, [IntPtr]::Zero) | Out-Null
                    Start-Sleep -Milliseconds 10
                }
                $synchronizedPaints = (Get-NativeProbe $window 20) - $syncBefore
                if ($synchronizedPaints -lt 3 -or $synchronizedPaints -gt 8) { throw "Vex frame scheduling: synchronized output starvation/pacing failed ($synchronizedPaints frames in 600 ms)." }
            }
            Start-Sleep -Milliseconds 500
            $idleBefore = Get-NativeProbe $window 20
            $process.Refresh(); $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
            Start-Sleep -Milliseconds 2000
            $process.Refresh()
            $idleCpu = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
            $idlePaints = (Get-NativeProbe $window 20) - $idleBefore
            if ($idlePaints -ne 0) { throw "Vex frame scheduling: $version trial $($trial + 1) idle rendered $idlePaints frames; blink=$(Get-NativeProbe $window 72) wake=$(Get-NativeProbe $window 75) active=$(Get-NativeProbe $window 76) status=$(Get-NativeProbe $window 31)." }
            $samples += @{ Version = $version; Trial = $trial + 1; FrameSpacingMs = $FrameSpacingMs; RequestToScene = (Get-TimingSummary $requests); ScenePreparation = (Get-TimingSummary $scenes); RequestMs = $requests; SceneMs = $scenes; IdlePaints = $idlePaints; SynchronizedPaintsOver600Ms = $synchronizedPaints; IdleCpuMsOverTwoSeconds = $idleCpu }
            Close-NativeInstance $process $window
            $process = $null
        }
    }
    @{ Result = 'COMPLETE'; Samples = $samples; Scope = 'Alternating same-cwd raw cmd; cursor animation disabled in both versions; ten warm-up frames; window-thread QPC measurements.'; Limitations = @('synthetic requests through the production invalidation path, not keyboard-to-photon latency', 'scene preparation excludes GPU execution, end-pass submission and Present', 'not scrolling/resize frame-time or cold-start measurements', 'idle assertions require a quiet desktop; real focus and pointer changes legitimately repaint chrome') } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'frame-scheduling.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
