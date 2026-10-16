param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\picker-performance",
    [ValidateRange(100,16384)][int]$Entries = 8192
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$NativeWorkingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Vex picker benchmark ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $NativeWorkingDirectory | Out-Null
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$previousSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
try {
    for ($i = 0; $i -lt $Entries; $i++) { [IO.File]::WriteAllBytes((Join-Path $NativeWorkingDirectory ('file-{0:D5}.txt' -f $i)), [byte[]]@()) }
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-NativeAction $window 12
    $openMs = $watch.Elapsed.TotalMilliseconds
    $slowestProbe = 0.0
    do {
        $sample = [Diagnostics.Stopwatch]::StartNew()
        $loading = Get-NativeProbe $window 104
        $slowestProbe = [Math]::Max($slowestProbe, $sample.Elapsed.TotalMilliseconds)
        if (!$loading) { break }
        Start-Sleep -Milliseconds 5
    } while ($watch.Elapsed.TotalSeconds -lt 20)
    if ($loading) { throw 'Vex picker benchmark: index timed out.' }
    $indexReadyMs = $watch.Elapsed.TotalMilliseconds
    Assert-NativeProbe $window 105 $Entries 'all benchmark paths are indexed'
    Assert-NativeProbe $window 55 50 'result list stays bounded'
    $queries = @()
    for ($i = 0; $i -lt 7; $i++) {
        $query = 'file-{0:D5}' -f (($i * 997) % $Entries)
        $watch.Restart()
        Send-NativeText $window $query
        for ($j = 0; $j -lt $query.Length; $j++) {
            [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]8, [IntPtr]::Zero) | Out-Null
        }
        $queries += $watch.Elapsed.TotalMilliseconds
    }
    $process.Refresh()
    $report = [pscustomobject]@{
        Entries = $Entries; OpenRequestMs = $openMs; IndexReadyMs = $indexReadyMs
        SlowestUiProbeWhileIndexingMs = $slowestProbe; TypeAndEraseTenCharactersMs = $queries
        WorkingSetBytes = $process.WorkingSet64
        Limitations = @('one local-filesystem sample, not cold-cache or network IO', 'index readiness includes the 250 ms metadata timer', 'synchronous window-message round trips, not input-to-present latency', 'not a baseline comparison or a universal latency guarantee')
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'result.json')
    $report | ConvertTo-Json -Depth 4
    Close-NativeInstance $process $window
    $process = $null
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $previousSelftest
    Remove-Item $NativeWorkingDirectory -Recurse -Force
}
