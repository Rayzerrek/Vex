param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\modal-updates"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\ModalHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
function Flush-ModalPaint([IntPtr]$Dialog) {
    [VexNativeSmoke]::SendWindowMessage($Dialog, 0x8004, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
function Save-ModalScreenImage([IntPtr]$Dialog, [string]$Path) {
    $bounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($Dialog, [ref]$bounds) | Out-Null
    $image = New-Object Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($image)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $image.Size)
        $image.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $image.Dispose() }
}
function Get-ModalPaintCount([IntPtr]$Dialog, [int]$Query = 1) {
    return [VexNativeSmoke]::SendWindowMessage($Dialog, 0x8002, [IntPtr]$Query, [IntPtr]::Zero).ToInt64()
}
try {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path $OutputDirectory).Path
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process; $window = $instance.Window
    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window
    $query = [VexNativeSmoke]::GetDlgItem($dialog, 102)
    $bounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($dialog, [ref]$bounds) | Out-Null
    [VexNativeSmoke]::SetControlText($query, 'split') | Out-Null
    Flush-ModalPaint $dialog
    $surfaceBefore = Get-ModalPaintCount $dialog 2
    $allBefore = Get-ModalPaintCount $dialog
    [VexNativeSmoke]::SetControlText($query, 'split ') | Out-Null
    Flush-ModalPaint $dialog
    $sameResultsPaints = (Get-ModalPaintCount $dialog) - $allBefore
    $surfacePaints = (Get-ModalPaintCount $dialog 2) - $surfaceBefore
    if ($sameResultsPaints -ne 0 -or $surfacePaints -ne 0) { throw "Vex palette update: equivalent query repainted $sameResultsPaints owner-drawn controls and $surfacePaints surfaces." }
    $surfaceBefore = Get-ModalPaintCount $dialog 2
    $terminalPaintsBefore = Get-NativeProbe $window 20
    [VexNativeSmoke]::SetControlText($query, 'split r') | Out-Null
    Flush-ModalPaint $dialog
    if ((Get-ModalPaintCount $dialog 2) -ne $surfaceBefore) { throw 'Vex palette update: typing repaints the popup surface instead of only the results.' }
    if ((Get-NativeProbe $window 20) -ne $terminalPaintsBefore) { throw 'Vex palette update: filtering repaints the terminal behind the popup.' }
    $afterBounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($dialog, [ref]$afterBounds) | Out-Null
    if ($bounds.Left -ne $afterBounds.Left -or $bounds.Top -ne $afterBounds.Top -or $bounds.Right -ne $afterBounds.Right -or $bounds.Bottom -ne $afterBounds.Bottom) { throw 'Vex palette update: typing moved or resized the palette.' }
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-filtered.png')
    [VexNativeSmoke]::SetControlText($query, 'no-such-command') | Out-Null
    Flush-ModalPaint $dialog
    Assert-NativeProbe $window 101 0 'empty command results remain non-actionable'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-empty.png')
    [VexNativeSmoke]::SetControlText($query, 'split') | Out-Null
    Flush-ModalPaint $dialog
    if ((Get-NativeProbe $window 101) -eq 0) { throw 'Vex palette update: filtering did not recover after an empty result.' }
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'palette closes after partial updates'
    Invoke-NativeAction $window 19
    $dialog = Get-OwnedModal $window
    $timings = @()
    $themeRasterCount = $null
    for ($i = 0; $i -lt 20; $i++) {
        foreach ($page in @(131, 132, 130, 133)) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]$page, [IntPtr]::Zero) | Out-Null
            Flush-ModalPaint $dialog
            $watch.Stop()
            $timings += $watch.Elapsed.TotalMilliseconds
            if ($page -eq 131) {
                $currentRasterCount = Get-ModalPaintCount $dialog 0
                if ($null -ne $themeRasterCount -and $currentRasterCount -ne $themeRasterCount) { throw 'Vex settings update: unchanged theme cards were rasterized again.' }
                $themeRasterCount = $currentRasterCount
            }
            $before = Get-ModalPaintCount $dialog
            [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]$page, [IntPtr]::Zero) | Out-Null
            Flush-ModalPaint $dialog
            if ((Get-ModalPaintCount $dialog) -ne $before) { throw 'Vex settings update: clicking the selected page redraws the controls.' }
        }
    }
    foreach ($page in @(130, 131, 132)) {
        [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]$page, [IntPtr]::Zero) | Out-Null
        Flush-ModalPaint $dialog
        Start-Sleep -Milliseconds 100
        Save-ModalScreenImage $dialog (Join-Path $OutputDirectory "settings-live-$page.png")
    }
    $sorted = @($timings | Sort-Object)
    @{ Result = 'PASS'; EquivalentQueryOwnerDrawPaints = $sameResultsPaints; EquivalentQuerySurfacePaints = $surfacePaints; SettingsSwitchMedianMs = $sorted[40]; SettingsSwitchP95Ms = $sorted[75]; SettingsSwitchMaxMs = $sorted[-1]; SettingsSwitchMs = $timings; Checks = @('stable palette bounds', 'equivalent results do not repaint', 'filter updates only results', 'empty result recovery', 'selected page is a no-op', '80 settings page switches', 'no terminal repaint while filtering', 'theme card raster cache reuse'); Limitations = @('synchronous Win32 command/paint round trips, not physical click-to-photon latency') } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'modal-updates.json')
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'settings close after page updates'
    Close-NativeInstance $process $window
    $process = $null
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
