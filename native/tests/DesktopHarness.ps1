Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class VexNativeSmoke {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeoutW(IntPtr window, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    public static IntPtr SendWindowMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam) {
        IntPtr result;
        if (SendMessageTimeoutW(window, message, wparam, lparam, 3, 5000, out result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Vex native smoke: window message failed or timed out.");
        return result;
    }
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
function Send-NativeText([IntPtr]$Window, [string]$Text) {
    foreach ($unit in $Text.ToCharArray()) {
        [VexNativeSmoke]::SendWindowMessage($Window, 0x102, [IntPtr][int]$unit, [IntPtr]::Zero) | Out-Null
    }
}
function Send-NativeStatus([IntPtr]$Window, [string]$Body, [int]$DelayMs = 0) {
    $command = '@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "'
    $command += "Start-Sleep -Milliseconds $DelayMs;[Console]::Write([char]27 + ']7501;$Body' + [char]7)" + '"'
    Send-NativeText $Window ($command + "`r")
}
function Get-NativeProbe([IntPtr]$Window, [int]$Query) {
    return [VexNativeSmoke]::SendWindowMessage($Window, 0x8002, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
function Assert-NativeProbe([IntPtr]$Window, [int]$Query, [long]$Expected, [string]$Check) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $actual = Get-NativeProbe $Window $Query
        if ($actual -eq $Expected) { return }
        Start-Sleep -Milliseconds 30
    } until ([DateTime]::UtcNow -gt $deadline)
    throw "Vex native smoke: $Check (query=$Query expected=$Expected actual=$actual)."
}
function Invoke-NativeAction([IntPtr]$Window, [int]$Action, [int]$Index = 0) {
    [VexNativeSmoke]::SendWindowMessage($Window, 0x8005, [IntPtr]$Action, [IntPtr]$Index) | Out-Null
}
function Save-NativeScreenshot([IntPtr]$Window, [string]$Path) {
    [VexNativeSmoke]::SetForegroundWindow($Window) | Out-Null
    Start-Sleep -Milliseconds 300
    $bounds = New-Object VexNativeSmoke+Rect
    if (![VexNativeSmoke]::GetWindowRect($Window, [ref]$bounds)) { throw 'Vex native smoke: screenshot bounds unavailable.' }
    $bitmap = New-Object Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Start-NativeInstance([string]$Arguments = $NativeLaunchArguments, [string]$ExecutablePath = $Executable, [string]$WorkingDirectory = $NativeWorkingDirectory) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $start = @{ FilePath = $ExecutablePath; WorkingDirectory = $WorkingDirectory; PassThru = $true }
    if ($Arguments.Length -gt 0) { $start.ArgumentList = $Arguments }
    $process = Start-Process @start
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 20
        $process.Refresh()
        if ($process.HasExited) { throw 'Vex native smoke: process exited before opening its window.' }
    } until ($process.MainWindowHandle -ne [IntPtr]::Zero -or [DateTime]::UtcNow -gt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { $process.Kill(); throw 'Vex native smoke: window startup timed out.' }
    return [pscustomobject]@{ Process = $process; Window = $process.MainWindowHandle; ReadyMs = $watch.Elapsed.TotalMilliseconds }
}
function Close-NativeInstance($Process, [IntPtr]$Window) {
    [VexNativeSmoke]::SendWindowMessage($Window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if (!$Process.WaitForExit(5000)) { throw 'Vex native smoke: clean shutdown timed out.' }
    if ($Process.ExitCode -ne 0) { throw "Vex native smoke: unexpected exit code $($Process.ExitCode)." }
}
