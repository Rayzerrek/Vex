param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [int]$Samples = 9,
    [string]$TerminalShellId = 'cmd'
)

$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$runDirectory = Join-Path $OutputDirectory ('run-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$results = @()
for ($sample = 0; $sample -lt $Samples; $sample++) {
    $profile = Join-Path $runDirectory "profile-$sample"
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    @{ ShellId = $TerminalShellId; ConfirmOnExit = $false; Appearance = 'Dark' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json')
    $log = Join-Path $runDirectory "startup-$sample.log"
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('VEX_')) { $start.Environment.Remove($key) | Out-Null }
    }
    $start.Environment['VEX_PROFILE_DIR'] = $profile
    $start.Environment['VEX_STARTUP_DIAG'] = $log
    $process = [Diagnostics.Process]::Start($start)
    try {
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 100
            $lines = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log } else { @() }
            $render = $lines | Where-Object { $_ -match 'ms content rendered$' } | Select-Object -First 1
            $output = $lines | Where-Object { $_ -match 'ms (prewarmed )?first terminal output$' } | Select-Object -First 1
        } while (!$process.HasExited -and (!$render -or !$output) -and $deadline.Elapsed.TotalSeconds -lt 15)
        if (!$render -or !$output) { throw "Startup failed: $log (exit=$($process.HasExited))" }
        $offsetLine = $lines | Where-Object { $_ -match 'process start ([0-9.]+)ms before OnStartup' } | Select-Object -First 1
        $offsetLine -match '^([0-9.,]+)ms process start ([0-9.,]+)ms' | Out-Null
        $offset = [double]::Parse($Matches[2].Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) -
            [double]::Parse($Matches[1].Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)
        $render -match '^([0-9.,]+)ms' | Out-Null
        $renderMs = [double]::Parse($Matches[1].Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) + $offset
        $output -match '^([0-9.,]+)ms' | Out-Null
        $outputMs = [double]::Parse($Matches[1].Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) + $offset
        $result = [pscustomobject]@{ Sample = $sample; FirstFrameMs = $renderMs; FirstOutputMs = $outputMs }
        $results += $result
        $result | ConvertTo-Json -Compress
    }
    finally {
        if (!$process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (!$process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
        }
        $process.Dispose()
    }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
$ordered = @($results.FirstFrameMs | Sort-Object)
"Median first frame: $($ordered[[int][Math]::Floor($ordered.Count / 2)]) ms"
