param(
    [Parameter(Mandatory)][string]$AppExecutable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$TerminalShellId = 'nu',
    [string]$WorkingDirectory = $env:USERPROFILE,
    [ValidateRange(1, 20)][int]$Samples = 3
)

$ErrorActionPreference = 'Stop'
$AppExecutable = (Resolve-Path -LiteralPath $AppExecutable).Path
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('run-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
for ($sample = 0; $sample -lt $Samples; $sample++) {
    $profile = Join-Path $runDirectory "profile-$sample"
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    @{ ShellId = $TerminalShellId; ConfirmOnExit = $false; Appearance = 'Dark'; FontFamily = 'Cascadia Mono' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json')
    @{
        Windows = @(@{
            SelectedProjectIndex = 0
            Projects = @(@{
                Name = 'Prompt latency'
                WorkingDirectory = $WorkingDirectory
                DefaultShellId = $TerminalShellId
                SelectedTabIndex = 0
                Tabs = @(@{
                    Title = 'Terminal'
                    Root = @{ type = 'terminal'; ShellId = $TerminalShellId; WorkingDirectory = $WorkingDirectory; IsFocused = $true }
                })
            })
        })
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $profile 'session.json')

    $report = Join-Path $profile 'report.txt'
    $start = [Diagnostics.ProcessStartInfo]::new($AppExecutable)
    $start.UseShellExecute = $false
    foreach ($key in @($start.Environment.Keys)) {
        if ($key.StartsWith('VEX_')) { $start.Environment.Remove($key) | Out-Null }
    }
    $start.Environment['VEX_PROFILE_DIR'] = $profile
    $start.Environment['VEX_SELFTEST'] = $report
    $start.Environment['VEX_SELFTEST_PROMPT_LATENCY'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(45000)) { throw "Prompt benchmark timed out: $profile" }
        $result = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw } else { '' }
        if ($process.ExitCode -ne 0 -or $result -match 'FAIL|EXCEPTION' -or $result.TrimEnd() -notmatch 'done$') {
            throw "Prompt benchmark failed (exit=$($process.ExitCode)): $profile`n$result"
        }
        Get-Content -LiteralPath $report | Where-Object { $_.StartsWith('{') }
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
"Results: $runDirectory"
