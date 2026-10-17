param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [Parameter(Mandatory = $true)][string]$BaselineExecutable,
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\terminal-output",
    [ValidateRange(1000,100000)][int]$Rows = 6000,
    [ValidateRange(1,10)][int]$Trials = 3
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldWarp = $env:VEX_NATIVE_WARP
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
$samples = @()
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_WARP = $null
    foreach ($workload in @('plain', 'styled')) {
        $fixture = @'
$e = [char]27
$b = [Text.StringBuilder]::new()
for ($r = 0; $r -lt ROWS; $r++) {
    for ($c = 0; $c -lt 12; $c++) {
        if (STYLED) { [void]$b.Append($e + '[38;2;' + (90 + $r % 160) + ';' + (90 + $c * 12) + ';180m') }
        [void]$b.Append('abcdefghij')
    }
    [void]$b.Append($e + "[0m`r`n")
    if ($b.Length -ge 16384) { [Console]::Write($b.ToString()); [void]$b.Clear() }
}
[void]$b.Append("`r`nVEX_NATIVE_SMOKE_OK`r`n")
[Console]::Write($b.ToString())
'@
        $fixture = $fixture.Replace('ROWS', [string]$Rows).Replace('STYLED', $(if ($workload -eq 'styled') { '$true' } else { '$false' }))
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fixture))
        for ($trial = 0; $trial -lt $Trials; $trial++) {
            $versions = if ($trial % 2 -eq 0) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
            foreach ($version in $versions) {
                $path = if ($version -eq 'baseline') { $BaselineExecutable } else { $Executable }
                $instance = Start-NativeInstance -ExecutablePath (Resolve-Path $path).Path
                $process = $instance.Process
                $window = $instance.Window
                if ([VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]9, [IntPtr]::Zero).ToInt64() -eq 1) { throw 'Output benchmark: graphics-debug executables are not performance samples.' }
                if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1120, 720, 0x0040)) { throw 'Output benchmark: resize failed.' }
                Start-Sleep -Milliseconds 500
                Send-NativeText $window "@cls`r"
                Assert-NativeProbe $window 0 0 'output fixture starts without completion marker'
                $process.Refresh()
                $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
                $framesBefore = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]2, [IntPtr]::Zero).ToInt64()
                $watch = [Diagnostics.Stopwatch]::StartNew()
                Send-NativeText $window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -EncodedCommand ' + $encoded + "`r")
                # Keep diagnostics bounded; text probes materialize snapshots and affect the measured workload.
                $deadline = [DateTime]::UtcNow.AddSeconds(90)
                do {
                    Start-Sleep -Milliseconds 100
                    $frames = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]2, [IntPtr]::Zero).ToInt64()
                    # A bounded 10 Hz text probe confirms the final VT marker, never raw pipe bytes.
                    $done = (Get-NativeProbe $window 0) -eq 1
                } until ($done -or [DateTime]::UtcNow -gt $deadline)
                if (!$done) { throw 'Output benchmark: final output marker timed out.' }
                [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
                $watch.Stop()
                $process.Refresh()
                $samples += [pscustomobject]@{
                    Version = $version; Workload = $workload; Trial = $trial + 1; Rows = $Rows
                    WallMs = $watch.Elapsed.TotalMilliseconds
                    ProcessCpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
                    PresentedFrames = $frames - $framesBefore
                    PrivateBytes = $process.PrivateMemorySize64
                }
                Close-NativeInstance $process $window
                $process = $null
            }
        }
    }
    $report = [pscustomobject]@{
        Result = 'COMPLETE'; Samples = $samples
        Scope = 'Alternating real ConPTY scrolling output at 1120x720; ordinary VT and truecolor VT.'
        Limitations = @('Wall time includes command submission, child startup, ConPTY and up to 100 ms marker polling delay', 'CPU is the terminal process only, excluding shell/ConPTY children', 'Viewport probes can force snapshots at 10 Hz', 'not physical input-to-photon latency')
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'terminal-output.json')
    $report | ConvertTo-Json -Depth 5
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_WARP = $oldWarp
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
