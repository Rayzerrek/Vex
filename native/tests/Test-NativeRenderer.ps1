param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification",
    [switch]$RequireGraphicsDebug,
    [switch]$RequireNativeGlyphAtlas
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function Get-RendererProbe([IntPtr]$Window, [int]$Query) {
    return [VexNativeSmoke]::SendWindowMessage($Window, 0x8008, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
function Assert-RendererProbe([IntPtr]$Window, [int]$Query, [long]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $actual = Get-RendererProbe $Window $Query
        if ($actual -eq $Expected) { return }
        Start-Sleep -Milliseconds 30
    } until ([DateTime]::UtcNow -gt $deadline)
    throw "Vex renderer: query=$Query expected=$Expected actual=$actual."
}
function Assert-RendererIdle([IntPtr]$Window) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    $previous = Get-RendererProbe $Window 2
    $quietSamples = 0
    do {
        Start-Sleep -Milliseconds 250
        $current = Get-RendererProbe $Window 2
        $quietSamples = if ($current -eq $previous) { $quietSamples + 1 } else { 0 }
        if ($quietSamples -ge 4) { return }
        $previous = $current
    } until ([DateTime]::UtcNow -gt $deadline)
    throw 'Vex renderer: presentation did not become idle after shell/appearance updates settled.'
}
function Save-RendererCapture([IntPtr]$Window, [string]$Name) {
    $path = Join-Path $OutputDirectory "$Name.bmp"
    $capturePath = $env:VEX_NATIVE_CAPTURE_BMP
    Remove-Item $capturePath -ErrorAction SilentlyContinue
    $result = [VexNativeSmoke]::SendWindowMessage($Window, 0x8007, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    if ($result -ne 1 -or !(Test-Path $capturePath)) { throw 'Vex renderer: GPU readback failed; no screen/software fallback is allowed.' }
    Move-Item $capturePath $path -Force
    $image = [Drawing.Bitmap]::FromFile($path)
    try {
        $surface = Get-NativeSurface $Window
        if ($image.Width -ne [int][Math]::Round($surface.Width * $surface.Scale) -or
            $image.Height -ne [int][Math]::Round($surface.Height * $surface.Scale)) { throw 'Vex renderer: stale readback dimensions.' }
        $colors = New-Object 'System.Collections.Generic.HashSet[int]'
        $stepX = [Math]::Max(1, [int][Math]::Floor($image.Width / 100))
        $stepY = [Math]::Max(1, [int][Math]::Floor($image.Height / 80))
        for ($y = 0; $y -lt $image.Height; $y += $stepY) {
            for ($x = 0; $x -lt $image.Width; $x += $stepX) { $colors.Add($image.GetPixel($x, $y).ToArgb()) | Out-Null }
        }
        if ($colors.Count -lt 24) { throw 'Vex renderer: capture is blank or lacks rendered text/chrome.' }
        $image.Save((Join-Path $OutputDirectory "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $image.Dispose() }
    return (Get-FileHash $path -Algorithm SHA256).Hash
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldWarp = $env:VEX_NATIVE_WARP
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$oldGraphicsLog = $env:VEX_NATIVE_GRAPHICS_LOG
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
$results = @()
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.renderer-capture.bmp'
    $env:VEX_NATIVE_GRAPHICS_LOG = Join-Path $OutputDirectory 'renderer-debug.txt'
    Remove-Item $env:VEX_NATIVE_GRAPHICS_LOG -ErrorAction SilentlyContinue
    foreach ($driver in @('default', 'warp')) {
        $env:VEX_NATIVE_WARP = if ($driver -eq 'warp') { '1' } else { $null }
        $instance = Start-NativeInstance
        $process = $instance.Process
        $window = $instance.Window
        Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
        Assert-NativeProbe $window 0 1 'renderer fixture real shell round trip'
        Assert-RendererProbe $window 0 12
        if ($driver -eq 'warp') { Assert-RendererProbe $window 1 1 }
        $software = Get-RendererProbe $window 1
        $graphicsDebug = Get-RendererProbe $window 9
        if ($RequireGraphicsDebug -and $graphicsDebug -ne 1) { throw 'Vex renderer: this executable does not have D3D12 validation enabled.' }
        $shellPid = Get-NativeProbe $window 14
        Start-Sleep -Milliseconds 400
        $before = Save-RendererCapture $window "renderer-$driver"
        if ($RequireNativeGlyphAtlas) {
            Assert-RendererProbe $window 16 (1024 * 1024)
            Save-RendererCapture $window "renderer-$driver-warmup" | Out-Null
            $misses = Get-RendererProbe $window 12
            $allocations = Get-RendererProbe $window 13
            $hits = Get-RendererProbe $window 11
            $draws = Get-RendererProbe $window 10
            $framesBefore = Get-RendererProbe $window 2
            if ($misses -le 0 -or $hits -le 0) { throw 'Vex renderer: native glyph atlas did not rasterize/reuse glyphs.' }
            Save-RendererCapture $window "renderer-$driver-warm" | Out-Null
            Assert-RendererProbe $window 12 $misses
            Assert-RendererProbe $window 13 $allocations
            if ((Get-RendererProbe $window 11) -le $hits) { throw 'Vex renderer: warm frame did not reuse cached glyphs.' }
            $presented = [Math]::Max(1, (Get-RendererProbe $window 2) - $framesBefore)
            if ((Get-RendererProbe $window 10) - $draws -gt 2 * $presented) { throw 'Vex renderer: warm single-pane frames were not batched.' }
        }

        $generation = Get-RendererProbe $window 3
        for ($i = 0; $i -lt 4; $i++) {
            Get-RendererProbe $window 7 | Out-Null
            $generation++
            Assert-RendererProbe $window 3 $generation
            Assert-NativeProbe $window 14 $shellPid 'device recreation must not restart ConPTY'
            Assert-NativeProbe $window 0 1 'device recreation preserves the parsed viewport'
        }
        $after = Save-RendererCapture $window "renderer-$driver-recovered"
        if ($before -ne $after) { throw 'Vex renderer: the same scene changed pixels after device recreation.' }

        foreach ($size in @(@(900, 560), @(1200, 760), @(680, 440), @(1040, 680))) {
            if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, $size[0], $size[1], 0x0040)) { throw 'Vex renderer: resize failed.' }
            $surface = Get-NativeSurface $window
            Assert-RendererProbe $window 4 ([int][Math]::Round($surface.Width * $surface.Scale))
            Assert-RendererProbe $window 5 ([int][Math]::Round($surface.Height * $surface.Scale))
            Start-Sleep -Milliseconds 50
        }
        $previousColumns = Get-NativeProbe $window 43
        Get-RendererProbe $window 8 | Out-Null
        if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1120, 720, 0x0040)) { throw 'Vex renderer: recovery resize failed.' }
        $generation++
        Assert-RendererProbe $window 3 $generation
        if ((Get-NativeProbe $window 43) -le $previousColumns) { throw 'Vex renderer: recovered resize did not update terminal layout.' }
        Assert-NativeProbe $window 14 $shellPid 'resize recovery preserves ConPTY'
        [VexNativeSmoke]::SendDpiChangedAtCurrentBounds($window)
        Save-RendererCapture $window "renderer-$driver-resized" | Out-Null

        [VexNativeSmoke]::SendWindowMessage($window, 0x112, [IntPtr]0xf020, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 250
        $frames = Get-RendererProbe $window 2
        for ($i = 0; $i -lt 5; $i++) {
            [VexNativeSmoke]::SendWindowMessage($window, 0xf, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        }
        Assert-RendererProbe $window 2 $frames
        [VexNativeSmoke]::SendWindowMessage($window, 0x112, [IntPtr]0xf120, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 250
        Save-RendererCapture $window "renderer-$driver-restored" | Out-Null

        if ($RequireNativeGlyphAtlas) {
            $fixture = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$e = [char]27
$lines = @(
    ($e + '[2J' + $e + '[H' + 'ASCII: abcdefghijklmnopqrstuvwxyz 0123456789 !@#$%^&*()'),
    ($e + '[1mBold' + $e + '[0m normal ' + $e + '[3mItalic' + $e + '[1m Bold italic' + $e + '[0m'),
    ($e + '[4mUnderline' + $e + '[0m ' + $e + '[9mStrike' + $e + '[0m'),
    ($e + '[38;2;240;200;90mGold' + $e + '[48;2;23;75;54m Green background ' + $e + '[0m'),
    ('Unicode: ' + [char]0x017b + [char]0x00f3 + [char]0x0142 + [char]0x0107 + ' e' + [char]0x0301 + ' ' + [char]0x4e2d + [char]0x6587 + ' ' + [char]::ConvertFromUtf32(0x1f600)),
    ('Box: ' + [char]0x250c + ([string][char]0x2500 * 12) + [char]0x2510),
    ('Blocks: ' + [char]0x2588 + [char]0x2584 + [char]0x2591 + [char]0x2592 + [char]0x2593 + ' Powerline: ' + [char]0xe0b0 + [char]0xe0b2 + ' Braille: ' + [char]0x28ff),
    'VEX_NATIVE_SMOKE_OK'
)
[Console]::Write(($lines -join "`r`n") + "`r`n")
'@
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($fixture))
            Send-NativeText $window ("@cls`r")
            Assert-NativeProbe $window 0 0 'clear the old fixture marker before Unicode output'
            Send-NativeText $window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -EncodedCommand ' + $encoded + "`r")
            Assert-NativeProbe $window 0 1 'styled Unicode atlas fixture crosses ConPTY'
            Assert-RendererIdle $window
            Save-RendererCapture $window "renderer-$driver-glyphs" | Out-Null
            Save-RendererCapture $window "renderer-$driver-glyphs-warmup" | Out-Null
            $misses = Get-RendererProbe $window 12
            $allocations = Get-RendererProbe $window 13
            Save-RendererCapture $window "renderer-$driver-glyphs-warm" | Out-Null
            Assert-RendererProbe $window 12 $misses
            Assert-RendererProbe $window 13 $allocations
            for ($i = 0; $i -lt 5; $i++) { Invoke-NativeAction $window 14 }
            Assert-NativeProbe $window 54 9 'small-font atlas eviction fixture'
            $resets = Get-RendererProbe $window 15
            $churn = @'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$e = [char]27
$b = [Text.StringBuilder]::new($e + '[2J' + $e + '[H')
for ($i = 0; $i -lt 2049; $i++) { [void]$b.Append([char](0x4e00 + $i)); [void]$b.Append(' ') }
[void]$b.Append("`r`nVEX_NATIVE_SMOKE_OK`r`n")
[Console]::Write($b.ToString())
'@
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($churn))
            Send-NativeText $window ("@cls`r")
            Assert-NativeProbe $window 0 0 'clear the old marker before atlas churn'
            Send-NativeText $window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -EncodedCommand ' + $encoded + "`r")
            Assert-NativeProbe $window 0 1 'more unique CJK graphemes than cache entries'
            Save-RendererCapture $window "renderer-$driver-eviction" | Out-Null
            if ((Get-RendererProbe $window 15) -le $resets) { throw 'Vex renderer: the bounded glyph cache did not exercise eviction.' }
            Assert-NativeProbe $window 14 $shellPid 'atlas eviction preserves the shell'
            Invoke-NativeAction $window 15
            Send-NativeText $window ("@cls`r")
            Assert-NativeProbe $window 0 0 'clear the eviction fixture'
            Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
            Assert-NativeProbe $window 0 1 'shell still accepts input after atlas eviction'
        }
        Invoke-NativeAction $window 1
        Invoke-NativeAction $window 2
        Assert-NativeProbe $window 11 3 'three live panes after renderer resize/recovery'
        $sessions = Get-NativeProbe $window 13
        foreach ($action in @(9, 10, 11)) {
            Invoke-NativeAction $window $action
            Start-Sleep -Milliseconds 100
            Save-RendererCapture $window "renderer-$driver-overlay-$action" | Out-Null
            [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]27, [IntPtr]::Zero) | Out-Null
        }
        Invoke-NativeAction $window 10 7
        Save-RendererCapture $window "renderer-$driver-light" | Out-Null
        Invoke-NativeAction $window 10 1
        Invoke-NativeAction $window 13
        Invoke-NativeAction $window 15
        Assert-NativeProbe $window 13 $sessions 'appearance edits preserve all sessions'
        Assert-RendererIdle $window
        Assert-RendererProbe $window 6 0
        $process.Refresh()
        $localVideoMemory = Get-RendererProbe $window 20
        $sharedVideoMemory = Get-RendererProbe $window 21
        $results += [pscustomobject]@{
            Driver = $driver; Software = [bool]$software; GraphicsDebug = [bool]$graphicsDebug
            Generations = $generation; WorkingSetBytes = $process.WorkingSet64
            PrivateBytes = $process.PrivateMemorySize64
            LocalVideoMemoryBytes = if ($localVideoMemory -ge 0) { $localVideoMemory } else { $null }
            SharedVideoMemoryBytes = if ($sharedVideoMemory -ge 0) { $sharedVideoMemory } else { $null }
            GlyphHits = Get-RendererProbe $window 11; GlyphMisses = Get-RendererProbe $window 12
            DrawCalls = Get-RendererProbe $window 10
            UploadAllocations = Get-RendererProbe $window 13; UploadCapacityBytes = Get-RendererProbe $window 14
            Result = 'PASS'
        }
        Close-NativeInstance $process $window
        $process = $null
    }
    $env:VEX_NATIVE_SELFTEST = $null
    $instance = Start-NativeInstance
    $process = $instance.Process
    foreach ($query in @(0, 3, 7)) { Assert-RendererProbe $instance.Window $query 0 }
    Close-NativeInstance $process $instance.Window
    $process = $null
    $report = [pscustomobject]@{
        Result = 'PASS'
        Drivers = $results
        Checks = @('D3D12 presentation', 'hardware/default and forced WARP', 'GPU BGRA readback', 'pixel-identical healthy-device recreation', 'same live shell/viewport after recreation', 'resize including injected device reset/layout refresh', 'synthetic same-monitor DPI', 'no presents while minimized or idle', 'restore', 'three panes', 'palette/theme/tab-peek overlays', 'light theme/font zoom', 'debug-layer errors when enabled', 'selftest-only diagnostics')
        NativeGlyphAtlasChecks = [bool]$RequireNativeGlyphAtlas
        Limitations = @('recreation is injected, not physical GPU removal', 'no physical multi-monitor DPI or Windows 10 run', 'not a latency/performance comparison')
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'renderer.json')
    $report | ConvertTo-Json -Depth 5
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_WARP = $oldWarp
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture
    $env:VEX_NATIVE_GRAPHICS_LOG = $oldGraphicsLog
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
