param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\shell-painting"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\ModalHarness.ps1"
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class VexModalPixels {
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    public static int DifferentPixels(Bitmap first, Bitmap second) {
        if (first.Size != second.Size) throw new ArgumentException("Vex modal pixels: capture sizes differ.");
        var bounds = new Rectangle(0, 0, first.Width, first.Height);
        var a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
            var b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                var rowA = new byte[first.Width * 4]; var rowB = new byte[first.Width * 4];
                int different = 0;
                for (int y = 4; y < first.Height - 4; y++) {
                    Marshal.Copy(IntPtr.Add(a.Scan0, y * a.Stride), rowA, 0, rowA.Length);
                    Marshal.Copy(IntPtr.Add(b.Scan0, y * b.Stride), rowB, 0, rowB.Length);
                    for (int x = 4; x < first.Width - 4; x++) {
                        int p = x * 4;
                        if (rowA[p] != rowB[p] || rowA[p + 1] != rowB[p + 1] || rowA[p + 2] != rowB[p + 2]) different++;
                    }
                }
                return different;
            } finally { second.UnlockBits(b); }
        } finally { first.UnlockBits(a); }
    }
}
'@
function Get-ModalScreenBitmap([IntPtr]$Dialog) {
    if ([VexModalPixels]::DwmFlush() -lt 0) { throw 'Vex modal pixels: desktop compositor did not flush.' }
    $bounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($Dialog, [ref]$bounds) | Out-Null
    $image = New-Object Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($image)
    try { $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $image.Size) } finally { $graphics.Dispose() }
    return $image
}
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path $OutputDirectory).Path
    $env:VEX_NATIVE_SELFTEST = '1'
    $results = @()
    foreach ($theme in @('Vex Obsidian', 'Vex Alabaster')) {
        $settingsPath = Join-Path $OutputDirectory 'settings.json'
        [IO.File]::WriteAllText($settingsPath, (@{ theme = $theme } | ConvertTo-Json))
        $instance = Start-NativeInstance -SettingsPath $settingsPath
        $process = $instance.Process; $window = $instance.Window
        Invoke-NativeAction $window 18
        $dialog = Get-OwnedModal $window
        if (([VexNativeSmoke]::ExtendedStyle($dialog) -band 0x02000000) -eq 0) { throw 'Vex shell painting: popup descendants are not composited together.' }
        [VexNativeSmoke]::SetForegroundWindow($dialog) | Out-Null
        [VexNativeSmoke]::MoveCursorClient($dialog, 8, 8)
        Start-Sleep -Milliseconds 200
        $list = [VexNativeSmoke]::GetDlgItem($dialog, 103)
        $count = [VexNativeSmoke]::SendWindowMessage($list, 0x18B, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
        for ($i = 0; $i -lt 12; $i++) {
            if ($i -eq 6) {
                [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 820, 520, 0x0014) | Out-Null
                Start-Sleep -Milliseconds 200
            }
            $mode = [VexNativeSmoke]::GetDlgItem($dialog, (140 + $i % 4))
            Send-NativePointer $mode 0x0201 20 12 1
            Send-NativePointer $mode 0x0202 20 12 0
            Assert-NativeProbe $window 89 ($i % 4) 'shell mode click reaches the live owner'
            if ($i % 2 -eq 1) { [VexNativeSmoke]::SendWindowMessage($dialog, 0x28, $list, [IntPtr]1) | Out-Null }
            [VexNativeSmoke]::SendWindowMessage($list, 0x186, [IntPtr]($i % $count), [IntPtr]::Zero) | Out-Null
            [VexNativeSmoke]::SendWindowMessage($dialog, 0x8004, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            $before = Get-ModalScreenBitmap $dialog
            try {
                if (![VexModalPixels]::RedrawWindow($dialog, [IntPtr]::Zero, [IntPtr]::Zero, 0x181)) { throw 'Vex shell painting: reference repaint failed.' }
                $after = Get-ModalScreenBitmap $dialog
                try {
                    $different = [VexModalPixels]::DifferentPixels($before, $after)
                    if ($different -ne 0 -or $i -eq 11) {
                        $before.Save((Join-Path $OutputDirectory "$theme-$i-incremental.png"))
                        $after.Save((Join-Path $OutputDirectory "$theme-$i-full.png"))
                    }
                    if ($different -ne 0) { throw "Vex shell painting: $theme case $i has $different stale/different pixels after incremental updates." }
                    $results += @{ Theme = $theme; Case = $i; Compact = ($i -ge 6); DifferentPixels = $different }
                } finally { $after.Dispose() }
            } finally { $before.Dispose() }
        }
        Post-DialogKey $window 0x1B
        Assert-NativeProbe $window 53 0 'shell pixel fixture closes the picker'
        Close-NativeInstance $process $window
        $process = $null
    }
    @{ Result = 'PASS'; Cases = $results; Scope = 'Live desktop pixels after normal/compact row and segmented-mode updates compared with a forced full repaint of the same state.'; Limitations = @('requires a quiet unobscured desktop', 'four-pixel outer frame excluded', 'not a scanout/frame-time or multi-monitor guarantee') } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'shell-painting.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
