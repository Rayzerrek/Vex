param(
    [string]$Executable = (Join-Path $PSScriptRoot '../zig-out/bin/vex-native.exe')
)
$ErrorActionPreference = 'Stop'
# Requires an interactive Windows desktop and Git Bash. Configuration is isolated.
$Executable = (Resolve-Path $Executable).Path
if (-not ('VexCloseCheck' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VexCloseCheck {
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, UIntPtr w, IntPtr l,
        uint flags, uint timeout, out UIntPtr result);
}
'@
}
function Send-Vex([uint32]$Message, [uint64]$W = 0, [long]$L = 0) {
    [UIntPtr]$result = [UIntPtr]::Zero
    if ([VexCloseCheck]::SendMessageTimeout($script:window, $Message, [UIntPtr]$W, [IntPtr]$L,
        2, 15000, [ref]$result) -eq [IntPtr]::Zero) { throw 'Window timeout' }
    return $result.ToUInt64()
}
function Probe([uint32]$Query) { Send-Vex 0x8002 $Query }
$root = Join-Path ([IO.Path]::GetTempPath()) ('vex-close-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
$config = Join-Path $root 'init.lua'
[IO.File]::WriteAllText($config, "vex.config.confirm_close = true`nvex.config.cursor_blink = false`n")
$names = @('VEX_NATIVE_SETTINGS_PATH', 'VEX_NATIVE_SELFTEST', 'VEX_NATIVE_WARP')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$process = $null
try {
    $env:VEX_NATIVE_SETTINGS_PATH = $config
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_WARP = '1'
    $process = Start-Process -FilePath $Executable -ArgumentList '--shell gitbash' -WorkingDirectory (Split-Path $Executable) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()
        if ($process.HasExited) { throw "Startup exit $($process.ExitCode)" }
        $script:window = $process.MainWindowHandle
    } until ($window -ne [IntPtr]::Zero -or [DateTime]::UtcNow -gt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'No application window' }
    Start-Sleep -Seconds 3
    if ((Probe 85) -ne 5) { throw 'Git Bash unavailable: selected shell is not Git Bash' }
    if ((Probe 125) -ne 1) { throw 'Confirmation disabled' }
    $null = Send-Vex 0x8005 0
    Start-Sleep -Seconds 3
    $null = Send-Vex 0x8005 16 1
    Start-Sleep -Milliseconds 400
    if ((Probe 53) -ne 0 -or (Probe 10) -ne 1) { throw 'Idle Git Bash incorrectly requested confirmation' }
    Write-Host 'PASS: idle Git Bash closes without confirmation'
    $null = Send-Vex 0x8005 0
    Start-Sleep -Seconds 3
    foreach ($character in 'sleep 30'.ToCharArray()) {
        $null = Send-Vex 0x102 ([uint32]$character)
        Start-Sleep -Milliseconds 20
    }
    $null = Send-Vex 0x102 13
    Start-Sleep -Seconds 2
    # Avoid a false positive from an unrelated foreground process or startup activity.
    $title = [Text.Encoding]::UTF8.GetString([BitConverter]::GetBytes([uint64](Probe 95))).Trim([char]0)
    if ($title -ne 'sleep') { throw "Expected foreground sleep, got '$title'" }
    $null = Send-Vex 0x8005 16 1
    Start-Sleep -Milliseconds 400
    if ((Probe 53) -ne 5 -or (Probe 10) -ne 2) { throw 'Active Git Bash command lost close protection' }
    Write-Host 'PASS: active Git Bash command still requests confirmation'
} finally {
    if ($process -and !$process.HasExited) { & taskkill.exe /PID $process.Id /T /F | Out-Null }
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Remove-Item $root -Recurse -Force
}
