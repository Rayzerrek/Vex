# Requires an interactive Windows desktop. This opt-in test replaces clipboard text.
# Test the single-file Release bundle: ordinary unit tests retain framework DLLs
# that the publish pruning target can accidentally remove.
param([string]$AppPath)

$ErrorActionPreference = "Stop"
$root = Join-Path ([IO.Path]::GetTempPath()) ("vex-clipboard-test-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root | Out-Null
$report = Join-Path $root "report.txt"
$process = $null
$variables = @(@([Environment]::GetEnvironmentVariables().Keys) |
    Where-Object { $_.StartsWith("VEX_", [StringComparison]::Ordinal) }) +
    @("VEX_SELFTEST", "VEX_SELFTEST_CLIPBOARD", "VEX_PROFILE_DIR", "VEX_LIVE")
$variables = @($variables | Select-Object -Unique)
$saved = @{}
foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $null) }
    if (-not $AppPath) {
        $output = Join-Path $root "app"
        dotnet publish (Join-Path $PSScriptRoot "Vex.App") -c Release -p:DefineConstants=VEX_SELFTEST -o $output
        if ($LASTEXITCODE -ne 0) { throw "Clipboard test publish failed" }
        $AppPath = Join-Path $output "Vex.App.exe"
    }
    $env:VEX_SELFTEST = $report
    $env:VEX_SELFTEST_CLIPBOARD = "1"
    $env:VEX_PROFILE_DIR = Join-Path $root "profile"
    $env:VEX_LIVE = "0"
    $process = Start-Process -FilePath (Resolve-Path $AppPath).Path -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Clipboard test timed out; artifacts: $root" }
    $process.Refresh()
    $result = if (Test-Path $report) { Get-Content $report -Raw } else { "No report" }
    if ($process.ExitCode -ne 0 -or $result -match "FAIL|EXCEPTION" -or $result.TrimEnd() -notmatch "done$") {
        throw "Published clipboard test failed; artifacts: $root`n$result"
    }
    Write-Host $result
    # The console host can release its executable shortly after the app exits.
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            Remove-Item $root -Recurse -Force
            break
        }
        catch {
            if ($attempt -eq 19) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}
finally {
    if ($process -and -not $process.HasExited) { $process.Kill() }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
}
