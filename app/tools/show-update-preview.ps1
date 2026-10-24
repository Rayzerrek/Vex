param(
    [string]$Executable = (Join-Path $env:LOCALAPPDATA 'Programs/Vex/Vex.exe'),
    [switch]$WithNotice
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path $Executable).Path
$App = Split-Path $PSScriptRoot -Parent
$Output = Join-Path $App 'publish/update-preview'
$null = New-Item -ItemType Directory -Force $Output
$config = Join-Path $Output 'init.lua'
[IO.File]::WriteAllText($config, "vex.config.cursor_blink = false`nvex.config.confirm_close = false`nvex.config.appearance = 'dark'`n")
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VexUpdatePreview {
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, UIntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SetWindowText(IntPtr h, string title);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
}
'@
function Send-Preview([uint32]$Message, [uint64]$W = 0) {
    [UIntPtr]$result = [UIntPtr]::Zero
    if ([VexUpdatePreview]::SendMessageTimeout($script:window,$Message,[UIntPtr]$W,[IntPtr]::Zero,2,10000,[ref]$result) -eq [IntPtr]::Zero) { throw 'Preview window did not respond' }
    $result.ToUInt64()
}
$names = @('VEX_NATIVE_SELFTEST','VEX_NATIVE_SETTINGS_PATH','VEX_NATIVE_CAPTURE_BMP')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
try {
    # Same installed renderer/UI, but both real downloads and MSI installation are disabled.
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_SETTINGS_PATH = $config
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $Output 'preview.bmp'
    $process = Start-Process $Executable -ArgumentList '--shell cmd' -WorkingDirectory $App -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 50
        $process.Refresh()
        if ($process.HasExited) { throw "Preview exited: $($process.ExitCode)" }
        $window = $process.MainWindowHandle
    } until ($window -ne [IntPtr]::Zero -or [DateTime]::UtcNow -gt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'No preview window' }
    $null = [VexUpdatePreview]::SetWindowText($window, 'Vex - Update preview (MOCK, installation disabled)')
    Start-Sleep -Milliseconds 750
    $command = '@echo off & cls & echo Vex / native workspace & echo. & echo [MOCK] Update preview - installation is disabled. & echo This is the installed MSI build, not an image mockup. & echo. & echo [ok] Background download / SHA-256 verification & echo [ok] Acknowledged restart / rollback protection & echo [ok] Shared notices / X / hand cursor / short animations & echo. & echo X dismisses notices. Ctrl+Shift+T opens a new terminal.'
    foreach ($character in $command.ToCharArray()) { $null = Send-Preview 0x102 ([uint32]$character) }
    $null = Send-Preview 0x102 13
    Start-Sleep -Milliseconds 750
    $null = Send-Preview 0x8008 73
    Start-Sleep -Milliseconds 1200
    $null = Send-Preview 0x8008 74
    Start-Sleep -Milliseconds 800
    $null = Send-Preview 0x8008 75
    if ($WithNotice) {
        foreach ($character in 'exit 0'.ToCharArray()) { $null = Send-Preview 0x102 ([uint32]$character) }
        $null = Send-Preview 0x102 13
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ((Send-Preview 0x8002 70) -ne 1) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Demo shell did not finish' }
            Start-Sleep -Milliseconds 50
        }
    }
    Start-Sleep -Milliseconds 250
    $null = Send-Preview 0x8009 2
    if ((Send-Preview 0x8007) -ne 1) { throw 'GPU capture failed' }
    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Bitmap]::new((Join-Path $Output 'preview.bmp'))
    try { $image.Save((Join-Path $Output 'preview.png'), [Drawing.Imaging.ImageFormat]::Png) } finally { $image.Dispose() }
    $null = [VexUpdatePreview]::ShowWindow($window, 9)
    $null = [VexUpdatePreview]::SetForegroundWindow($window)
    Write-Host "Preview left open: PID=$($process.Id), installed executable=$Executable"
    Write-Host "Real GPU screenshot: $Output\preview.png"
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
}
