param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin/published-selftest')
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Repeat without changes: skipped bundle generation must retain native DLLs.
foreach ($attempt in 1..2) {
    dotnet publish (Join-Path $PSScriptRoot '../Vex.App') -c Release -o $OutputDirectory '-p:DefineConstants=VEX_SELFTEST'
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the self-test app failed' }
}
foreach ($payloadFile in 'Vex.App.r2r.dll', 'ghostty-vt.dll', 'conpty.dll', 'wpfgfx_cor3.dll', 'x64/OpenConsole.exe', 'ShellIntegration/Vex.cmd', 'ShellIntegration/Vex.nu') {
    if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory $payloadFile))) {
        throw "Published payload missing: $payloadFile"
    }
}

$runDirectory = Join-Path $OutputDirectory ('checks-' + [Guid]::NewGuid().ToString('N'))
$scenarios = @(
    @{ Name = 'core'; Incremental = '0'; Appearance = 'Dark' },
    @{ Name = 'incremental'; Incremental = '1'; Appearance = 'Dark' },
    @{ Name = 'corrupt-profile'; Incremental = '0'; Appearance = 'Dark'; Session = '{"Windows":[{"Projects":[null]}]}' },
    @{ Name = 'light-startup'; Incremental = '0'; Appearance = 'Light'; StartupOnly = $true }
)
foreach ($scenario in $scenarios) {
    $profile = Join-Path $runDirectory $scenario.Name
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    @{ ShellId = 'cmd'; ConfirmOnExit = $false; Appearance = $scenario.Appearance } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json')
    if ($scenario.Session) {
        Set-Content -LiteralPath (Join-Path $profile 'session.json') -Value $scenario.Session
    }
    $report = Join-Path $profile 'report.txt'
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $OutputDirectory 'Vex.App.exe'))
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('VEX_')) { $start.Environment.Remove($key) | Out-Null }
    }
    $start.Environment['VEX_PROFILE_DIR'] = $profile
    $start.Environment['VEX_SELFTEST'] = $report
    $start.Environment['VEX_SELFTEST_INCR'] = $scenario.Incremental
    if ($scenario.StartupOnly) { $start.Environment['VEX_SELFTEST_STARTUP'] = '1' }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.WaitForExit(500) -and $timer.Elapsed.TotalSeconds -lt 60) { }
        if (!$process.HasExited) { throw "Published scenario timed out: $profile" }
        $result = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { '' }
        if ($process.ExitCode -ne 0 -or $result -match 'FAIL|EXCEPTION' -or $result.TrimEnd() -notmatch 'done$') {
            $failures = ($result -split '\r?\n') | Where-Object { $_ -match 'FAIL|EXCEPTION' } | Select-Object -First 3
            throw "Published scenario failed (exit=$($process.ExitCode)): $profile`n$($failures -join "`n")"
        }
        "PASS $($scenario.Name): $report"
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
