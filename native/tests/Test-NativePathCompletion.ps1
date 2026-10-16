param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$Executable = (Resolve-Path $Executable).Path
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$NativeWorkingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Vex paths ' + [char]0x017b + ' ' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $NativeWorkingDirectory | Out-Null
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$process = $null
try {
    for ($i = 99; $i -ge 0; $i--) {
        $body = if ($i -eq 0) { 'VEX_NATIVE_SMOKE_OK' } elseif ($i -eq 99) { 'VEX_NATIVE_PANE_B' } else { "file $i" }
        [IO.File]::WriteAllText((Join-Path $NativeWorkingDirectory ('file-{0:D5}.txt' -f $i)), $body + "`r`n", [Text.Encoding]::ASCII)
    }
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory 'path-completion.bmp'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Start-Sleep -Milliseconds 400

    Send-NativeText $window '@type '
    Invoke-NativeAction $window 12
    Assert-NativeProbe $window 53 4 'path picker opens over a real cmd session'
    Assert-NativeProbe $window 55 50 'empty query retains exactly fifty of one hundred files'
    $capture = [VexNativeSmoke]::SendWindowMessage($window, 0x8007, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    if ($capture -ne 1) { throw 'Vex paths: GPU capture of completion results failed.' }
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]13, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 53 0 'Enter inserts the selected full path and closes the picker'
    Send-NativeText $window "`r"
    Assert-NativeProbe $window 0 1 'first alphabetical result executes with Unicode and spaced path quoting'

    Send-NativeText $window '@type '
    Invoke-NativeAction $window 12
    Send-NativeText $window 'no-match'
    Assert-NativeProbe $window 55 0 'unmatched query clears stale results'
    for ($i = 0; $i -lt 8; $i++) {
        [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]8, [IntPtr]::Zero) | Out-Null
    }
    Assert-NativeProbe $window 55 50 'backspace restores the bounded result list'
    Send-NativeText $window '099'
    Assert-NativeProbe $window 55 1 'query finds a file beyond the initial fifty'
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]13, [IntPtr]::Zero) | Out-Null
    Send-NativeText $window "`r"
    Assert-NativeProbe $window 3 1 'filtered result inserts the correct path into the live shell'

    Send-NativeText $window '@type '
    Invoke-NativeAction $window 12
    Send-NativeText $window ('a' * 127)
    Send-NativeText $window ([char]::ConvertFromUtf32(0x1f600))
    for ($i = 0; $i -lt 127; $i++) {
        [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]8, [IntPtr]::Zero) | Out-Null
    }
    Assert-NativeProbe $window 55 50 'query limit rejects a whole emoji rather than leaving a partial UTF-8 character'
    Invoke-NativeAction $window 12 99
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]27, [IntPtr]::Zero) | Out-Null

    Close-NativeInstance $process $window
    $process = $null
    $report = [pscustomobject]@{
        Result = 'PASS'
        Checks = @('live cmd completion', 'bounded fifty results', 'alphabetical default selection', 'empty matches and backspace recovery', 'filter beyond initial result limit', 'Unicode and spaced full-path insertion', 'whole-codepoint query limit and backspace recovery', 'GPU popup capture')
        Limitations = @('synthetic keyboard messages, not physical keyboard or IME', 'directory scanning remains synchronous and bounded, not background indexing')
    }
    $report | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutputDirectory 'path-completion.json')
    $report | ConvertTo-Json -Depth 3
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture
    Remove-Item $NativeWorkingDirectory -Recurse -Force
}
