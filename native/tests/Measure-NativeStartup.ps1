param(
    [string]$Executable = '',
    [Parameter(Mandatory = $true)][string]$BaselineExecutable,
    [string]$OutputDirectory = '',
    [ValidateRange(3,30)][int]$Trials = 7
)
$ErrorActionPreference = 'Stop'
if (!$Executable) { $Executable = Join-Path $PSScriptRoot '..\zig-out\bin\vex-native.exe' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\zig-out\verification\startup-performance' }
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldWarp = $env:VEX_NATIVE_WARP
$oldMetrics = $env:VEX_NATIVE_METRICS
$process = $null
$samples = @()
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_WARP = $null
    for ($trial = 0; $trial -lt $Trials; $trial++) {
        $versions = if ($trial % 2 -eq 0) { @('baseline', 'native-ui') } else { @('native-ui', 'baseline') }
        foreach ($version in $versions) {
            $path = (Resolve-Path -LiteralPath $(if ($version -eq 'baseline') { $BaselineExecutable } else { $Executable })).Path
            Write-Host "Measuring startup $version / trial $($trial + 1): $path"
            $env:VEX_NATIVE_METRICS = Join-Path $OutputDirectory "$version-$trial.txt"
            Remove-Item $env:VEX_NATIVE_METRICS -ErrorAction SilentlyContinue
            $instance = Start-NativeInstance -ExecutablePath $path
            $process = $instance.Process
            $window = $instance.Window
            if ([VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]9, [IntPtr]::Zero).ToInt64() -eq 1) {
                throw 'Vex startup: graphics-debug executables cannot be benchmarked.'
            }
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            $firstFrameMs = $null
            do {
                if (Test-Path $env:VEX_NATIVE_METRICS) {
                    foreach ($line in [IO.File]::ReadAllLines($env:VEX_NATIVE_METRICS)) {
                        if ($line -match '^Vex native first frame: ([0-9.]+) ms$') {
                            $firstFrameMs = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
                        }
                    }
                }
                if ($null -ne $firstFrameMs) { break }
                if ($process.HasExited) { throw 'Vex startup: application exited before its first presented frame.' }
                Start-Sleep -Milliseconds 20
            } until ([DateTime]::UtcNow -gt $deadline)
            if ($null -eq $firstFrameMs) { throw 'Vex startup: first presented frame metric timed out.' }
            Start-Sleep -Milliseconds 300
            Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
            Assert-NativeProbe $window 0 1 "startup $version trial $($trial + 1) reaches a usable real terminal"
            Start-Sleep -Milliseconds 800
            $process.Refresh()
            $samples += [pscustomobject]@{
                Version = $version; Trial = $trial + 1; WindowObservedMs = $instance.ReadyMs
                MainToFirstPresentedFrameMs = $firstFrameMs
                WorkingSetBytes = $process.WorkingSet64; PrivateBytes = $process.PrivateMemorySize64
            }
            Close-NativeInstance $process $window
            $process = $null
        }
    }
    $report = [pscustomobject]@{
        Result = 'COMPLETE'; Samples = $samples
        Scope = 'Alternating same-cwd raw cmd launches; identical metrics enabled; each instance must present and accept real terminal input.'
        Limitations = @('OS and driver caches are not flushed: these are warm launches, not cold-start measurements', 'window observation uses 20 ms polling and is not a presentation timestamp', 'frame metric starts at Zig main, not OS process creation', 'working set excludes GPU and child processes', 'not input-to-present latency or a WPF comparison')
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'startup-comparison.json')
    $report | ConvertTo-Json -Depth 4
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_WARP = $oldWarp
    $env:VEX_NATIVE_METRICS = $oldMetrics
}
