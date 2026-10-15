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
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    public static void SendDpiChangedAtCurrentBounds(IntPtr window) {
        Rect rect;
        uint dpi = GetDpiForWindow(window);
        if (dpi == 0 || dpi > 65535 || !GetWindowRect(window, out rect))
            throw new InvalidOperationException("Vex native smoke: DPI fixture metrics unavailable.");
        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Rect)));
        try {
            Marshal.StructureToPtr(rect, buffer, false);
            SendWindowMessage(window, 0x02e0, new IntPtr(dpi | (dpi << 16)), buffer);
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
}
'@
function Send-NativeText([IntPtr]$Window, [string]$Text) {
    foreach ($unit in $Text.ToCharArray()) {
        [VexNativeSmoke]::SendWindowMessage($Window, 0x102, [IntPtr][int]$unit, [IntPtr]::Zero) | Out-Null
    }
}
function Get-NativeSurface([IntPtr]$Window) {
    $rect = New-Object VexNativeSmoke+Rect
    $dpi = [VexNativeSmoke]::GetDpiForWindow($Window)
    if ($dpi -eq 0 -or ![VexNativeSmoke]::GetClientRect($Window, [ref]$rect)) { throw 'Vex native smoke: client metrics unavailable.' }
    $scale = $dpi / 96.0
    return [pscustomobject]@{ Width = ($rect.Right - $rect.Left) / $scale; Height = ($rect.Bottom - $rect.Top) / $scale; Scale = $scale }
}
function Get-NativePointerLParam([IntPtr]$Window, [double]$X, [double]$Y) {
    $scale = (Get-NativeSurface $Window).Scale
    $px = [int][Math]::Round($X * $scale)
    $py = [int][Math]::Round($Y * $scale)
    if ($px -lt -32768 -or $px -gt 32767 -or $py -lt -32768 -or $py -gt 32767) { throw 'Vex native smoke: pointer fixture exceeds signed Win32 coordinates.' }
    $packed = ([long]($py -band 0xffff) * 65536) -bor ($px -band 0xffff)
    return [IntPtr]$packed
}
function Send-NativePointer([IntPtr]$Window, [uint32]$Message, [double]$X, [double]$Y, [int]$Buttons = 0) {
    [VexNativeSmoke]::SendWindowMessage($Window, $Message, [IntPtr]$Buttons, (Get-NativePointerLParam $Window $X $Y)) | Out-Null
}
function Invoke-NativeTabGesture([IntPtr]$Window, [ValidateRange(0,7)][int]$Source, [double]$X, [double]$Y = 20, [ValidateRange(0,8)][int]$Operation = 0) {
    $raw = [VexNativeSmoke]::SendWindowMessage($Window, 0x8006, [IntPtr](($Operation -shl 8) -bor $Source), (Get-NativePointerLParam $Window $X $Y)).ToInt64()
    return [pscustomobject]@{ Pressed = $raw -band 3; CapturedPress = ($raw -band 4) -ne 0; Moved = ($raw -shr 3) -band 3; CapturedMove = ($raw -band 32) -ne 0; Destination = ($raw -shr 8) -band 15; SourceIdBeforeDrop = $raw -shr 32 }
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
    Start-Sleep -Milliseconds 150
    $dir = Split-Path -Parent $Path
    if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $bmpPath = Join-Path $dir '.capture.bmp'
    Remove-Item $bmpPath -ErrorAction SilentlyContinue
    $captured = [VexNativeSmoke]::SendWindowMessage($Window, 0x8007, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    if ($captured -eq 1 -and (Test-Path $bmpPath)) {
        $bytes = [IO.File]::ReadAllBytes($bmpPath)
        $stream = New-Object IO.MemoryStream(,$bytes)
        try {
            $img = [Drawing.Image]::FromStream($stream)
            try {
                $img.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $img.Dispose() }
        } finally {
            $stream.Dispose()
            Remove-Item $bmpPath -ErrorAction SilentlyContinue
        }
        return
    }
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
