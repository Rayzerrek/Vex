param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'

function Get-NativeSurface([IntPtr]$Window) {
    $rect = New-Object VexNativeSmoke+Rect
    $dpi = [VexNativeSmoke]::GetDpiForWindow($Window)
    if ($dpi -eq 0 -or ![VexNativeSmoke]::GetClientRect($Window, [ref]$rect)) { throw 'Vex native dividers: client metrics unavailable.' }
    $scale = $dpi / 96.0
    return [pscustomobject]@{ Width = ($rect.Right - $rect.Left) / $scale; Height = ($rect.Bottom - $rect.Top) / $scale; Scale = $scale }
}
function Send-NativePointer([IntPtr]$Window, [uint32]$Message, [double]$X, [double]$Y, [int]$Buttons = 0) {
    $scale = (Get-NativeSurface $Window).Scale
    $px = [int][Math]::Round($X * $scale)
    $py = [int][Math]::Round($Y * $scale)
    if ($px -lt -32768 -or $px -gt 32767 -or $py -lt -32768 -or $py -gt 32767) { throw 'Vex native dividers: pointer fixture exceeds signed Win32 coordinates.' }
    $packed = ([long]($py -band 0xffff) * 65536) -bor ($px -band 0xffff)
    [VexNativeSmoke]::SendWindowMessage($Window, $Message, [IntPtr]$Buttons, [IntPtr]$packed) | Out-Null
}
function Get-NativeRootHandleX([IntPtr]$Window) {
    return 6 + ((Get-NativeSurface $Window).Width - 18) * (Get-NativeProbe $Window 40) / 1000.0 + 3
}
function Start-NativeDividerDrag([IntPtr]$Window, [double]$X, [double]$Y) {
    [VexNativeSmoke]::SetForegroundWindow($Window) | Out-Null
    Send-NativePointer $Window 0x201 $X $Y 1
    Assert-NativeProbe $Window 41 1 'Zig owns an active divider drag'
    Assert-NativeProbe $Window 42 1 'Win32 owns mouse capture'
}
function Assert-NativeDragEnded([IntPtr]$Window) {
    Assert-NativeProbe $Window 41 0 'Zig drag state cleared'
    Assert-NativeProbe $Window 42 0 'Win32 capture released'
}
function Assert-NativeConsoleSize([IntPtr]$Window) {
    $columns = Get-NativeProbe $Window 43
    $rows = Get-NativeProbe $Window 44
    Send-NativeText $Window "@cls`r"
    Assert-NativeProbe $Window 35 0 'previous size marker cleared'
    $command = '@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "'
    $command += "if ([Console]::WindowWidth -eq $columns -and [Console]::WindowHeight -eq $rows) {[Console]::WriteLine('VEX_NATIVE_SHELL_'+'READY')}" + '"'
    Send-NativeText $Window ($command + "`r")
    Assert-NativeProbe $Window 35 1 'real ConPTY dimensions match the resized emulator'
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
try {
    $previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $scale = (Get-NativeSurface $window).Scale
    if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 60, 60, [int](960 * $scale), [int](600 * $scale), 0x0040)) { throw 'Vex native dividers: initial resize failed.' }
    Invoke-NativeAction $window 1
    Assert-NativeProbe $window 40 500 'initial right divider'
    $active = Get-NativeProbe $window 12
    $columns = Get-NativeProbe $window 43
    $x = Get-NativeRootHandleX $window
    Start-NativeDividerDrag $window $x 150
    Send-NativePointer $window 0x200 ($x + 90) 150 1
    if ((Get-NativeProbe $window 43) -ge $columns) { throw 'Vex native dividers: right pane columns did not shrink during live drag.' }
    Assert-NativeProbe $window 12 $active 'divider drag preserves terminal focus'
    Send-NativePointer $window 0x202 ($x + 90) 150
    Assert-NativeDragEnded $window
    $committed = Get-NativeProbe $window 40
    Assert-NativeConsoleSize $window

    foreach ($cancel in @(0x100, 0x215, 0x1f, 0x8)) {
        $x = Get-NativeRootHandleX $window
        Start-NativeDividerDrag $window $x 150
        Send-NativePointer $window 0x200 ($x + 50) 150 1
        $key = if ($cancel -eq 0x100) { 27 } else { 0 }
        $consumed = [VexNativeSmoke]::SendWindowMessage($window, $cancel, [IntPtr]$key, [IntPtr]::Zero).ToInt64()
        if ($cancel -eq 0x100 -and $consumed -ne 1) { throw 'Vex native dividers: Escape would be translated into terminal input.' }
        Assert-NativeDragEnded $window
        Assert-NativeProbe $window 40 $committed 'Escape/capture/cancel-mode/focus rollback'
        # A cancelled release over the close button must not close the application.
        Send-NativePointer $window 0x202 ((Get-NativeSurface $window).Width - 20) 20
        Assert-NativeProbe $window 11 2 'cancelled release cannot activate chrome'
    }

    $x = Get-NativeRootHandleX $window
    Start-NativeDividerDrag $window $x 150
    Send-NativePointer $window 0x200 ($x + 50) 150 1
    if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 60, 60, [int](1100 * $scale), [int](700 * $scale), 0x0040)) { throw 'Vex native dividers: drag-time resize failed.' }
    Assert-NativeDragEnded $window
    Assert-NativeProbe $window 40 $committed 'window resize rolls back captured drag'
    Send-NativePointer $window 0x202 10 150

    Start-NativeDividerDrag $window (Get-NativeRootHandleX $window) 150
    Send-NativePointer $window 0x200 ((Get-NativeRootHandleX $window) + 50) 150 1
    [VexNativeSmoke]::SendDpiChangedAtCurrentBounds($window)
    Assert-NativeDragEnded $window
    Assert-NativeProbe $window 40 $committed 'DPI message without physical resize rolls back captured drag'
    Send-NativePointer $window 0x202 10 150

    foreach ($outside in @(-300, 3000)) {
        Start-NativeDividerDrag $window (Get-NativeRootHandleX $window) 150
        Send-NativePointer $window 0x200 $outside 150 1
        Send-NativePointer $window 0x202 $outside 150
        Assert-NativeDragEnded $window
        $ratio = (Get-NativeProbe $window 40) / 1000.0
        $available = (Get-NativeSurface $window).Width - 18
        if ($ratio -lt 0.099 -or $ratio -gt 0.901 -or $available * [Math]::Min($ratio, 1 - $ratio) -lt 99) { throw 'Vex native dividers: outside drag violated ratio/minimum limits.' }
    }
    # Move back to the center before adding a nested down split.
    Start-NativeDividerDrag $window (Get-NativeRootHandleX $window) 150
    $center = (Get-NativeSurface $window).Width / 2
    Send-NativePointer $window 0x202 $center 150
    Invoke-NativeAction $window 2
    Assert-NativeProbe $window 11 3 'nested down split'
    $rootRatio = Get-NativeProbe $window 40
    $rows = Get-NativeProbe $window 44
    $surface = Get-NativeSurface $window
    $nestedX = (Get-NativeRootHandleX $window) + 100
    $nestedY = 44 + ($surface.Height - 56) / 2 + 3
    Start-NativeDividerDrag $window $nestedX $nestedY
    Send-NativePointer $window 0x200 $nestedX ($nestedY + 60) 1
    if ((Get-NativeProbe $window 44) -ge $rows) { throw 'Vex native dividers: nested pane rows did not shrink during drag.' }
    Send-NativePointer $window 0x202 $nestedX ($nestedY + 60)
    Assert-NativeDragEnded $window
    Assert-NativeProbe $window 40 $rootRatio 'nested drag does not move ancestor divider'
    Assert-NativeConsoleSize $window

    Start-NativeDividerDrag $window (Get-NativeRootHandleX $window) 150
    Send-NativePointer $window 0x200 ((Get-NativeRootHandleX $window) + 40) 150 1
    Invoke-NativeAction $window 0
    Assert-NativeDragEnded $window
    Invoke-NativeAction $window 6 0
    Assert-NativeProbe $window 40 $rootRatio 'tab change rolls back old divider'
    Start-NativeDividerDrag $window (Get-NativeRootHandleX $window) 150
    Send-NativePointer $window 0x200 ((Get-NativeRootHandleX $window) + 40) 150 1
    Invoke-NativeAction $window 3
    Assert-NativeDragEnded $window
    Assert-NativeProbe $window 11 2 'closing a captured tree cannot retain stale targets'
    Assert-NativeProbe $window 40 $rootRatio 'close rolls back ancestor before node mutation'
    Send-NativePointer $window 0x200 900 150
    Assert-NativeProbe $window 40 $rootRatio 'stale move cannot modify surviving divider'

    Start-NativeDividerDrag $window (Get-NativeRootHandleX $window) 150
    Send-NativeText $window ('@for /l %i in (1,1,1000000) do @echo DIVIDER_FLOOD_%i' + "`r")
    $origin = Get-NativeRootHandleX $window
    for ($step = 0; $step -lt 40; $step++) {
        Send-NativePointer $window 0x200 ($origin + ($step % 10) * 5) 150 1
        Assert-NativeProbe $window 41 1 'active-output drag remains responsive'
    }
    $verifiedDpi = [VexNativeSmoke]::GetDpiForWindow($window)
    Close-NativeInstance $process $window
    $process = $null
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    @{ result = 'PASS'; checks = 'nested live resize, actual ConPTY size, focus, outside bounds, rollback, chrome release, resize/DPI, tab/close and flooding captured shutdown'; dpi = $verifiedDpi } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'dividers.json')
    Write-Host 'Vex native dividers: PASS'
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    if ($previousDpiContext -and $previousDpiContext -ne [IntPtr]::Zero) { [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null }
}
