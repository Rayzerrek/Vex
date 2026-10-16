param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\native-ui-pixels",
    [switch]$RequireGraphicsDebug
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function Get-UiRendererProbe([IntPtr]$Window, [int]$Query) {
    return [VexNativeSmoke]::SendWindowMessage($Window, 0x8008, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
function Assert-UiPixel($Image, [int]$X, [int]$Y, [int]$Rgb, [int]$Tolerance = 1) {
    $actual = $Image.GetPixel($X, $Y)
    $expected = [Drawing.Color]::FromArgb($Rgb)
    if ([Math]::Abs([int]$actual.R - $expected.R) -gt $Tolerance -or
        [Math]::Abs([int]$actual.G - $expected.G) -gt $Tolerance -or
        [Math]::Abs([int]$actual.B - $expected.B) -gt $Tolerance) {
        throw "Vex UI pixels: ($X,$Y) expected RGB=$Rgb actual=$actual."
    }
}
function Save-UiCapture([IntPtr]$Window, [string]$Name) {
    Remove-Item $env:VEX_NATIVE_CAPTURE_BMP -ErrorAction SilentlyContinue
    if ((Get-UiRendererProbe $Window 999) -ne 0) { throw 'Vex UI pixels: unsupported diagnostic tag did not reject.' }
    if ([VexNativeSmoke]::SendWindowMessage($Window, 0x8007, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -ne 1) {
        throw 'Vex UI pixels: native GPU readback failed.'
    }
    $path = Join-Path $OutputDirectory "$Name.bmp"
    Copy-Item $env:VEX_NATIVE_CAPTURE_BMP $path -Force
    return $path
}
function Assert-UiScene([string]$Path) {
    $image = [Drawing.Bitmap]::FromFile($Path)
    try {
        foreach ($pixel in @(
            @(100, 90, 0xff0000), @(20, 60, 0x101010),
            @(300, 95, 0x101010), @(300, 60, 0x0000ff),
            @(100, 170, 0x00ff00), @(260, 180, 0xffcc00),
            @(380, 180, 0x101010), @(340, 180, 0x0000ff),
            @(10, 250, 0x101010), @(30, 260, 0xff8000), @(80, 260, 0x00ffff),
            @(150, 270, 0xff0000), @(170, 270, 0x101010), @(185, 255, 0x0000ff),
            @(260, 250, 0x880808), @(18, 350, 0x101010), @(1062, 350, 0x101010)
        )) { Assert-UiPixel $image $pixel[0] $pixel[1] $pixel[2] }
        foreach ($left in @(24, 518, 1038)) {
            $ink = 0
            for ($x = $left; $x -lt $left + 18; $x++) {
                for ($y = 340; $y -lt 380; $y++) {
                    $color = $image.GetPixel($x, $y)
                    if ($color.R -gt 80 -and $color.G -gt 80 -and $color.B -gt 80) { $ink++ }
                }
            }
            if ($ink -lt 10) { throw "Vex UI pixels: missing shaped text near atlas tile boundary x=$left." }
        }
        $image.Save([IO.Path]::ChangeExtension($Path, '.png'), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $image.Dispose() }
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldWarp = $env:VEX_NATIVE_WARP
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$oldLog = $env:VEX_NATIVE_GRAPHICS_LOG
$process = $null
$results = @()
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.ui-capture.bmp'
    $env:VEX_NATIVE_GRAPHICS_LOG = Join-Path $OutputDirectory 'ui-debug.txt'
    Remove-Item $env:VEX_NATIVE_GRAPHICS_LOG -ErrorAction SilentlyContinue
    foreach ($driver in @('default', 'warp')) {
        $env:VEX_NATIVE_WARP = if ($driver -eq 'warp') { '1' } else { $null }
        $instance = Start-NativeInstance
        $process = $instance.Process
        $window = $instance.Window
        [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1120, 720, 0x0040) | Out-Null
        Start-Sleep -Milliseconds 300
        Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
        Assert-NativeProbe $window 0 1 'UI pixel fixture retains a real shell'
        $pidBefore = Get-NativeProbe $window 14
        $debug = Get-UiRendererProbe $window 9
        if ($RequireGraphicsDebug -and $debug -ne 1) { throw 'Vex UI pixels: graphics validation is required.' }
        Get-UiRendererProbe $window 40 | Out-Null
        $first = Save-UiCapture $window "ui-$driver"
        Assert-UiScene $first
        $warm = Save-UiCapture $window "ui-$driver-warm"
        if ((Get-FileHash $first).Hash -ne (Get-FileHash $warm).Hash) { throw 'Vex UI pixels: warm redraw changed native shape/text pixels.' }
        $resets = Get-UiRendererProbe $window 26
        Get-UiRendererProbe $window 42 | Out-Null
        $churn = Save-UiCapture $window "ui-$driver-evicted"
        if ((Get-UiRendererProbe $window 26) -le $resets) { throw 'Vex UI pixels: bounded text cache did not evict under 2,300 distinct labels.' }
        Assert-UiScene $churn
        if ((Get-FileHash $first).Hash -ne (Get-FileHash $churn).Hash) { throw 'Vex UI pixels: text atlas eviction corrupted earlier draws or final text.' }
        Get-UiRendererProbe $window 41 | Out-Null
        Assert-NativeProbe $window 14 $pidBefore 'UI cache churn does not restart the shell'
        Assert-NativeProbe $window 0 1 'real terminal survives diagnostic UI scene'
        $errors = Get-UiRendererProbe $window 6
        if ($errors -ne 0) { throw "Vex UI pixels: graphics validation reported $errors errors." }
        $results += [pscustomobject]@{ Driver = $driver; GraphicsDebug = [bool]$debug; Result = 'PASS' }
        Close-NativeInstance $process $window
        $process = $null
    }
    $report = [pscustomobject]@{
        Result = 'PASS'; Drivers = $results
        Checks = @('GPU pixels for rounded fill/stroke, diagonal line and ellipses', 'nested clipping and restoration', 'alpha blending', 'text across multiple bounded atlas tiles', 'warm pixel stability', '2,300-label mid-pass atlas eviction preserves earlier and final pixels', 'same live shell')
        Limitations = @('selftest-only scene through production native drawing primitives, not WPF/Rex visual parity', 'text seam probes detect missing ink, not subpixel glyph equality with DirectWrite', 'not physical GPU loss, multi-monitor DPI or latency measurement')
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'ui-pixels.json')
    $report | ConvertTo-Json -Depth 4
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest; $env:VEX_NATIVE_WARP = $oldWarp
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture; $env:VEX_NATIVE_GRAPHICS_LOG = $oldLog
}
