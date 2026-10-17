param(
    [string]$Executable = '',
    [Parameter(Mandatory = $true)][string]$BaselineExecutable,
    [string]$OutputDirectory = '',
    [ValidateRange(60,1200)][int]$Frames = 180,
    [ValidateRange(1,10)][int]$Trials = 3
)
$ErrorActionPreference = 'Stop'
if (!$Executable) { $Executable = Join-Path $PSScriptRoot '..\zig-out\bin\vex-native.exe' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\zig-out\verification\atlas-performance' }
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldWarp = $env:VEX_NATIVE_WARP
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
$results = @()
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_WARP = $null
    foreach ($workload in @('plain', 'styled')) {
        # Alternate versions between trials rather than running all baseline trials first.
        for ($trial = 0; $trial -lt $Trials; $trial++) {
            $versions = if ($trial % 2 -eq 0) { @('baseline', 'atlas') } else { @('atlas', 'baseline') }
            foreach ($version in $versions) {
                $path = if ($version -eq 'baseline') { $BaselineExecutable } else { $Executable }
                $path = (Resolve-Path -LiteralPath $path).Path
                Write-Host "Measuring $version / $workload / trial $($trial + 1): $path"
                $instance = Start-NativeInstance -ExecutablePath $path
                $process = $instance.Process
                $window = $instance.Window
                if ([VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]9, [IntPtr]::Zero).ToInt64() -eq 1) { throw 'Vex benchmark: do not measure a graphics-debug executable.' }
                if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1120, 720, 0x0040)) { throw 'Vex benchmark: window resize failed.' }
                Send-NativeText $window ("@cls`r")
                $fixture = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$e = [char]27
$b = [Text.StringBuilder]::new()
[void]$b.Append($e + '[2J' + $e + '[H')
for ($row = 0; $row -lt 28; $row++) {
    for ($col = 0; $col -lt 96; $col++) {
        if (STYLED) {
            [void]$b.Append($e + '[0;' + (1 + ($col % 2) * 2) + ';38;2;' + (100 + $col % 155) + ';' + (100 + $row % 155) + ';180m')
        }
        [void]$b.Append([char](65 + (($row + $col) % 26)))
    }
    [void]$b.Append($e + "[0m`r`n")
}
[void]$b.Append('VEX_NATIVE_SMOKE_OK' + "`r`n")
[Console]::Write($b.ToString())
'@
                $fixture = $fixture.Replace('STYLED', $(if ($workload -eq 'styled') { '$true' } else { '$false' }))
                $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fixture))
                Send-NativeText $window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -EncodedCommand ' + $encoded + "`r")
                Assert-NativeProbe $window 0 1 'benchmark fixture reached the actual viewport'
                Start-Sleep -Milliseconds 800
                for ($i = 0; $i -lt 20; $i++) { [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
                $process.Refresh()
                $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
                $watch = [Diagnostics.Stopwatch]::StartNew()
                for ($i = 0; $i -lt $Frames; $i++) { [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
                $watch.Stop()
                $process.Refresh()
                $cpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
                $activeWorkingSet = $process.WorkingSet64
                # Sample preparation separately so diagnostic round trips do not enter the CPU/frame sample.
                [VexNativeSmoke]::SendWindowMessage($window, 0x8009, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                $sceneSamples = @()
                for ($i = 0; $i -lt 120; $i++) {
                    [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                    $sceneSamples += [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]28, [IntPtr]::Zero).ToInt64()
                }
                $sceneSamples = @($sceneSamples | Sort-Object)
                Start-Sleep -Milliseconds 2600
                $process.Refresh()
                $results += [pscustomobject]@{
                    Version = $version; Workload = $workload; Trial = $trial + 1; Frames = $Frames
                    ProcessCpuMs = $cpuMs; ProcessCpuMsPerFrame = $cpuMs / $Frames
                    WallMs = $watch.Elapsed.TotalMilliseconds
                    ScenePreparationMedianUs = $sceneSamples[60]; ScenePreparationP95Us = $sceneSamples[113]
                    ScenePreparationMaxUs = $sceneSamples[119]
                    GeometryHits = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]50, [IntPtr]::Zero).ToInt64()
                    GeometryCapacityBytes = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]52, [IntPtr]::Zero).ToInt64()
                    ActiveWorkingSetBytes = $activeWorkingSet; IdleWorkingSetBytes = $process.WorkingSet64
                    PrivateBytes = $process.PrivateMemorySize64
                    NativeGlyphAtlas = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]16, [IntPtr]::Zero).ToInt64() -gt 0
                    UploadCapacityBytes = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]14, [IntPtr]::Zero).ToInt64()
                    UploadTrims = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]17, [IntPtr]::Zero).ToInt64()
                }
                Close-NativeInstance $process $window
                $process = $null
            }
        }
    }
    $report = [pscustomobject]@{
        Result = 'COMPLETE'; Samples = $results
        Scope = 'Same-cwd, 1120x720, raw cmd, real ConPTY fixtures, warm forced-paint CPU/memory samples; versions alternate between trials.'
        Limitations = @('CPU includes driver/UI work, not GPU duration', 'Present may pace frames to the display', 'not input-to-present latency or cold-start distributions', 'working set excludes GPU and children', 'not controlled laboratory measurements')
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'comparison.json')
    $report | ConvertTo-Json -Depth 5
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_WARP = $oldWarp
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
