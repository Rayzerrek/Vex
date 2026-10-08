param(
    [Parameter(Mandatory)][string]$AppExecutable,
    [Parameter(Mandatory)][string]$ParserExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Variant = 'current',
    [string]$NeovimExecutable = (Get-Command nvim -ErrorAction SilentlyContinue).Source
)

$ErrorActionPreference = 'Stop'
$AppExecutable = (Resolve-Path -LiteralPath $AppExecutable).Path
$ParserExecutable = (Resolve-Path -LiteralPath $ParserExecutable).Path
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('run-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null

& $ParserExecutable --feed $Variant | Set-Content -LiteralPath (Join-Path $runDirectory 'feed.jsonl')
if ($LASTEXITCODE -ne 0) { throw 'VT feed benchmark failed' }
& $ParserExecutable | Set-Content -LiteralPath (Join-Path $runDirectory 'snapshot.txt')
if ($LASTEXITCODE -ne 0) { throw 'Viewport snapshot benchmark failed' }

foreach ($mode in @('renderer', 'smoothness')) {
    $profile = Join-Path $runDirectory $mode
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    @{ ShellId = 'cmd'; ConfirmOnExit = $false; Appearance = 'Dark'; FontFamily = 'Cascadia Mono'; FontSize = 14 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json')
    $report = Join-Path $profile 'report.txt'
    $start = [Diagnostics.ProcessStartInfo]::new($AppExecutable)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('VEX_')) { $start.Environment.Remove($key) | Out-Null }
    }
    $start.Environment['VEX_PROFILE_DIR'] = $profile
    $start.Environment['VEX_SELFTEST'] = $report
    $start.Environment['VEX_SELFTEST_BENCH'] = if ($mode -eq 'renderer') { '1' } else { '0' }
    $start.Environment['VEX_SELFTEST_SMOOTHNESS'] = if ($mode -eq 'smoothness') { '1' } else { '0' }
    if ($NeovimExecutable) { $start.Environment['VEX_PERF_NVIM'] = (Resolve-Path -LiteralPath $NeovimExecutable).Path }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(45000)) { throw "Benchmark timed out: $profile" }
        $result = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { '' }
        if ($process.ExitCode -ne 0 -or $result -match 'FAIL|EXCEPTION' -or $result.TrimEnd() -notmatch 'done$') {
            throw "Benchmark failed (exit=$($process.ExitCode)): $profile`n$result"
        }
        "PASS ${mode}: $report"
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
"Results: $runDirectory"
