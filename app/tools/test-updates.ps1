param(
    [string]$Executable = (Join-Path $PSScriptRoot '../zig-out/bin/vex-native.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../publish/update-ui'),
    [switch]$Light
)
$ErrorActionPreference = 'Stop'
# Local interactive rendering test. Self-test mode cannot download or install updates.
$Executable = (Resolve-Path $Executable).Path
$null = New-Item -ItemType Directory -Force $OutputDirectory
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VexUpdateDesktop {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$root = Join-Path ([IO.Path]::GetTempPath()) ('vex-update-ui-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
$config = Join-Path $root 'init.lua'
$theme = if ($Light) { 'light' } else { 'dark' }
[IO.File]::WriteAllText($config, "vex.config.confirm_close = false`nvex.config.cursor_blink = false`nvex.config.appearance = '$theme'`n")
$names = @('VEX_NATIVE_SELFTEST', 'VEX_NATIVE_SETTINGS_PATH', 'VEX_NATIVE_CAPTURE_BMP')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$process = $null
$previousDpi = [IntPtr]::Zero
function Send-Vex([uint32]$Message, [uint64]$W = 0, [long]$L = 0) {
    [UIntPtr]$result = [UIntPtr]::Zero
    if ([VexUpdateDesktop]::SendMessageTimeout($script:window, $Message, [UIntPtr]$W, [IntPtr]$L, 2, 15000, [ref]$result) -eq [IntPtr]::Zero) { throw "Vex did not respond: $Message / $W" }
    $result.ToUInt64()
}
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_SETTINGS_PATH = $config
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory "$theme.bmp"
    $process = Start-Process $Executable -ArgumentList '--shell cmd' -WorkingDirectory (Split-Path $Executable) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "Vex exited: $($process.ExitCode)" }
        $window = $process.MainWindowHandle
    } until ($window -ne [IntPtr]::Zero -or [DateTime]::UtcNow -gt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'No Vex window' }
    Start-Sleep -Seconds 3
    $token = Send-Vex 0x8002 12
    $null = Send-Vex 0x8008 72
    if ((Send-Vex 0x8008 78) -ne 1) { throw 'Up-to-date notice is not visible' }
    Start-Sleep -Seconds 3
    if ((Send-Vex 0x8008 78) -ne 1) { throw 'Up-to-date notice expired too early' }
    # Rechecking must give the new result its own full lifetime.
    $null = Send-Vex 0x8008 71
    $null = Send-Vex 0x8008 72
    Start-Sleep -Seconds 3
    if ((Send-Vex 0x8008 78) -ne 1) { throw 'An earlier check expired the new result' }
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ((Send-Vex 0x8008 78) -ne 0) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Up-to-date notice did not expire automatically' }
        Start-Sleep -Milliseconds 100
    }
    Start-Sleep -Milliseconds 300
    $frames = Send-Vex 0x8008 2
    Start-Sleep -Seconds 1
    if ((Send-Vex 0x8008 2) -ne $frames) { throw 'Expired update notice keeps repainting' }
    $null = Send-Vex 0x8008 72
    $null = Send-Vex 0x8008 75
    Start-Sleep -Seconds 6
    if ((Send-Vex 0x8008 78) -ne 1) { throw 'Up-to-date expiry dismissed a ready update' }
    foreach ($state in (@(71..77) + @(80,81))) {
        if ((Send-Vex 0x8008 $state) -ne 1) { throw "Cannot set update state $state" }
        $null = Send-Vex 0x8009 2
        if ((Send-Vex 0x8008 78) -ne 1) { throw "Update state $state is not visible" }
        if ((Send-Vex 0x8002 12) -ne $token) { throw 'An update replaced the terminal session' }
        if ($state -eq 75) {
            Start-Sleep -Milliseconds 250
            $null = Send-Vex 0x8009 2
            if ((Send-Vex 0x8007) -ne 1) { throw 'Cannot capture update banner' }
        }
    }
    $null = Send-Vex 0x8008 75
    $null = Send-Vex 0x8009 2
    Start-Sleep -Milliseconds 500
    $frames = Send-Vex 0x8008 2
    Start-Sleep -Seconds 1
    if ((Send-Vex 0x8008 2) -ne $frames) { throw 'Idle update banner keeps repainting' }
    $previousDpi = [VexUpdateDesktop]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $rect = New-Object VexUpdateDesktop+Rect
    if (![VexUpdateDesktop]::GetClientRect($window, [ref]$rect)) { throw 'Cannot measure client' }
    $scale = [VexUpdateDesktop]::GetDpiForWindow($window) / 96.0
    foreach ($query in 90..93) {
        $null = Send-Vex 0x8008 $query
        Start-Sleep -Milliseconds 250
        $null = Send-Vex 0x8009 2
        if ((Send-Vex 0x8008 65) -ne 1 -or (Send-Vex 0x8008 78) -ne 1) { throw 'A transient notification replaced the pending update' }
        if ((Send-Vex 0x8007) -ne 1) { throw 'Cannot capture notification surface' }
        Copy-Item (Join-Path $OutputDirectory "$theme.bmp") (Join-Path $OutputDirectory "$theme-notice-$($query - 90).bmp") -Force
    }
    $null = Send-Vex 0x8008 96
    Start-Sleep -Milliseconds 250
    $glyphs = Send-Vex 0x8008 19
    $closeX = [int]($rect.right - 38 * $scale)
    $closeY = [int]($rect.bottom - 118 * $scale)
    # Exercise the real notification pointer route without moving the user's desktop cursor.
    $hit = Send-Vex 0x800c 0 ($closeX -bor ($closeY -shl 16))
    if ($hit -ne 3 -or (Send-Vex 0x8008 97) -ne 1) { throw "Notification X hit/cursor mismatch: hit=$hit, target=$closeX,$closeY" }
    $null = Send-Vex 0x8009 2
    if ((Send-Vex 0x8008 58) -eq 1 -and (Send-Vex 0x8008 95) -ne 1) { throw 'Close hover did not animate' }
    Start-Sleep -Milliseconds 250
    if ((Send-Vex 0x8008 19) -ne $glyphs) { throw 'Hover animation rerasterizes notification text' }
    $bodyX = [int]($rect.right - 120 * $scale)
    $bodyY = [int]($rect.bottom - 92 * $scale)
    if ((Send-Vex 0x800c 0 ($bodyX -bor ($bodyY -shl 16))) -ne 1 -or (Send-Vex 0x8008 97) -ne 0) { throw 'Notification body uses a clickable cursor' }
    $null = Send-Vex 0x201 0 ($bodyX -bor ($bodyY -shl 16))
    $null = Send-Vex 0x202 0 ($bodyX -bor ($bodyY -shl 16))
    if ((Send-Vex 0x8008 65) -ne 1) { throw 'Notification body click dismissed it instead of requiring X' }
    $null = Send-Vex 0x201 0 ($closeX -bor ($closeY -shl 16))
    $null = Send-Vex 0x202 0 ($closeX -bor ($closeY -shl 16))
    if ((Send-Vex 0x8008 65) -ne 0) { throw 'Notification X did not dismiss it' }
    Start-Sleep -Milliseconds 250
    if ((Send-Vex 0x8008 95) -ne 0) { throw 'Dismissed notification keeps its animation wake alive' }
    $null = Send-Vex 0x8008 98
    Start-Sleep -Milliseconds 250
    $null = Send-Vex 0x8009 2
    $glyphs = Send-Vex 0x8008 19
    $null = Send-Vex 0x8008 99
    $null = Send-Vex 0x8009 2
    if ((Send-Vex 0x8008 65) -ne 1 -or (Send-Vex 0x8008 19) -ne $glyphs) { throw 'Malformed UTF-8 partially replaced the visible notification' }
    $null = Send-Vex 0x8007
    Copy-Item (Join-Path $OutputDirectory "$theme.bmp") (Join-Path $OutputDirectory "$theme-long.bmp") -Force
    $longCloseY = [int]($rect.bottom - 203 * $scale)
    Add-Type -AssemblyName System.Drawing
    $bitmap = [Drawing.Bitmap]::new((Join-Path $OutputDirectory "$theme-long.bmp"))
    try {
        if ($bitmap.GetPixel($closeX - 10,$longCloseY - 10).ToArgb() -ne $bitmap.GetPixel($closeX - 20,$longCloseY - 10).ToArgb()) { throw 'A replaced notification retained hover on its relocated close button' }
    } finally { $bitmap.Dispose() }
    if ((Send-Vex 0x800c 0 ($closeX -bor ($longCloseY -shl 16))) -ne 3) { throw 'Long notification escaped its bounded six-line layout' }
    $null = Send-Vex 0x8008 94
    Start-Sleep -Milliseconds 250
    $x = [int]($rect.right - 38 * $scale)
    $y = [int]($rect.bottom - 38 * $scale)
    $position = $x -bor ($y -shl 16)
    $null = Send-Vex 0x201 0 $position
    $null = Send-Vex 0x202 0 $position
    if ((Send-Vex 0x8008 78) -ne 0) { throw 'Later did not dismiss the banner' }
    if ((Send-Vex 0x8002 12) -ne $token) { throw 'Dismissal replaced the terminal session' }
    foreach ($character in 'echo preserved output & exit 7'.ToCharArray()) { $null = Send-Vex 0x102 ([uint32]$character) }
    $null = Send-Vex 0x102 13
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ((Send-Vex 0x8002 70) -ne 1) { if ([DateTime]::UtcNow -gt $deadline) { throw 'Final session did not exit' }; Start-Sleep -Milliseconds 50 }
    if ((Send-Vex 0x8002 71) -ne 7 -or (Send-Vex 0x8008 65) -ne 1) { throw 'Final session exit did not use the common notification' }
    Start-Sleep -Milliseconds 250
    $null = Send-Vex 0x8007
    Copy-Item (Join-Path $OutputDirectory "$theme.bmp") (Join-Path $OutputDirectory "$theme-session-exit.bmp") -Force
    $null = Send-Vex 0x10
    if (!$process.WaitForExit(15000)) { throw "Vex did not shut down (PID $($process.Id))" }
    if ($process.ExitCode -ne 0) { throw "Vex exited with error $($process.ExitCode)" }
    Write-Host "Update UI ($theme): all states, up-to-date expiry/reset and persistent ready update, real GPU capture, idle paint budget, X/hand cursor/hover, retained text, Unicode/long/malformed content, session exit and shutdown passed."
} finally {
    if ($process) {
        $process.Refresh()
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    if ($previousDpi -ne [IntPtr]::Zero) { $null = [VexUpdateDesktop]::SetThreadDpiAwarenessContext($previousDpi) }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Remove-Item $root -Recurse -Force
}
