param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function Send-CursorMode([IntPtr]$Window, [string]$Sequence) {
    $command = '@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "'
    Send-NativeText $Window ($command + "[Console]::Write([char]27 + '$Sequence')" + '"' + "`r")
}
function Activate-CursorWindow([IntPtr]$Window) {
    [VexNativeSmoke]::SetForegroundWindow($Window) | Out-Null
    # Replay focus explicitly; these are window-message tests, not a physical keyboard focus test.
    [VexNativeSmoke]::SendWindowMessage($Window, 7, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $Window 76 1 'visible focused window can animate the cursor'
}
function Assert-CursorIdle([IntPtr]$Window) {
    Assert-NativeProbe $Window 75 0 'ineligible cursor owns no animation timer'
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    $previous = Get-NativeProbe $Window 20
    $quiet = 0
    do {
        Start-Sleep -Milliseconds 250
        $current = Get-NativeProbe $Window 20
        $quiet = if ($previous -eq $current) { $quiet + 1 } else { 0 }
        if ($quiet -ge 4) { return }
        $previous = $current
    } until ([DateTime]::UtcNow -gt $deadline)
    throw 'Vex cursor: steady/hidden/inactive cursor did not become idle.'
}
function Wait-CursorSnapshot([IntPtr]$Window) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    $previous = Get-NativeProbe $Window 79
    $quiet = 0
    do {
        Start-Sleep -Milliseconds 100
        $current = Get-NativeProbe $Window 79
        $quiet = if ($previous -eq $current) { $quiet + 1 } else { 0 }
        if ($quiet -ge 5) { return }
        $previous = $current
    } until ([DateTime]::UtcNow -gt $deadline)
    throw 'Vex cursor: terminal output did not settle before pixel comparison.'
}
function Save-CursorPhase([IntPtr]$Window, [int]$Phase, [string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Assert-NativeProbe $Window 73 $Phase 'cursor reaches requested phase'
        $result = [VexNativeSmoke]::SendWindowMessage($Window, 0x8007, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
        if ($result -ne 1) { throw 'Vex cursor: GPU readback failed.' }
        if ((Get-NativeProbe $Window 73) -eq $Phase) {
            $bytes = [IO.File]::ReadAllBytes($env:VEX_NATIVE_CAPTURE_BMP)
            $stream = New-Object IO.MemoryStream(,$bytes)
            $image = [Drawing.Bitmap]::FromStream($stream)
            $path = Join-Path $OutputDirectory "$Name.png"
            try {
                $image.Save($path, [Drawing.Imaging.ImageFormat]::Png)
                $surface = Get-NativeSurface $Window
                $cellWidth = (Get-NativeProbe $Window 83) / 1000.0
                $cellHeight = (Get-NativeProbe $Window 84) / 1000.0
                # Single-pane ASCII fixture: workspace top 44 DIPs, grid inset (6, 30).
                $x = [int][Math]::Floor((6 + (Get-NativeProbe $Window 81) * $cellWidth) * $surface.Scale) - 1
                $y = [int][Math]::Floor((74 + (Get-NativeProbe $Window 82) * $cellHeight) * $surface.Scale) - 1
                $rect = New-Object Drawing.Rectangle $x, $y, ([int][Math]::Ceiling($cellWidth * $surface.Scale) + 2), ([int][Math]::Ceiling($cellHeight * $surface.Scale) + 2)
                $crop = $image.Clone($rect, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $path = Join-Path $OutputDirectory "$Name-cell.png"
                try { $crop.Save($path, [Drawing.Imaging.ImageFormat]::Png) } finally { $crop.Dispose() }
            } finally {
                $image.Dispose()
                $stream.Dispose()
            }
            return (Get-FileHash $path -Algorithm SHA256).Hash
        }
    } until ([DateTime]::UtcNow -gt $deadline)
    throw 'Vex cursor: phase changed throughout GPU capture.'
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.cursor-capture.bmp'
    $instance = Start-NativeInstance -BlinkingCursor $true
    $process = $instance.Process
    $window = $instance.Window
    Activate-CursorWindow $window
    Assert-NativeProbe $window 72 1 'cursor blinking is enabled by default'
    Assert-NativeProbe $window 77 1 'Ghostty default cursor mode blinks'
    Assert-NativeProbe $window 75 1 'eligible cursor arms a native wake'
    Wait-CursorSnapshot $window
    $shown = Save-CursorPhase $window 1 'cursor-shown'
    $generation = Get-NativeProbe $window 79
    $hidden = Save-CursorPhase $window 0 'cursor-hidden'
    if ($shown -eq $hidden) { throw 'Vex cursor: visible/hidden phases produced identical GPU pixels.' }
    Assert-NativeProbe $window 79 $generation 'blink frames reuse the cached terminal snapshot'

    Send-CursorMode $window '[6 q'
    Assert-NativeProbe $window 77 0 'DECSCUSR selects a steady bar'
    Assert-CursorIdle $window
    Send-CursorMode $window '[5 q'
    Assert-NativeProbe $window 77 1 'DECSCUSR enables blinking again'
    Assert-NativeProbe $window 75 1 'blinking restarts after the steady mode'
    Send-CursorMode $window '[?25l'
    Assert-NativeProbe $window 78 0 'DEC 25 hides the cursor'
    Assert-CursorIdle $window
    Send-CursorMode $window '[?25h'
    Assert-NativeProbe $window 75 1 'revealing a blinking cursor restarts its wake'

    Assert-NativeProbe $window 73 0 'cursor reaches its hidden phase before typing'
    Send-NativeText $window ' '
    Assert-NativeProbe $window 73 1 'typing reveals the cursor immediately'
    Send-NativeText $window "`b"
    Send-CursorMode $window '[1 q'
    Assert-NativeProbe $window 80 1 'DECSCUSR selects a real block cursor'
    Assert-NativeProbe $window 75 1 'block cursor can blink'
    Wait-CursorSnapshot $window
    $shown = Save-CursorPhase $window 1 'cursor-block-shown'
    $generation = Get-NativeProbe $window 79
    $hidden = Save-CursorPhase $window 0 'cursor-block-hidden'
    Assert-NativeProbe $window 79 $generation 'block phases compare the same terminal snapshot'
    if ($shown -eq $hidden) { throw 'Vex cursor: block cursor did not restore underlying cells.' }

    Invoke-NativeAction $window 9
    Assert-NativeProbe $window 53 1 'command palette is open'
    Assert-CursorIdle $window
    $dialog = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $popup = [VexNativeSmoke]::GetLastActivePopup($window)
        if ($popup -ne $window -and $popup -ne [IntPtr]::Zero) { $dialog = $popup; break }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)
    if (!$dialog) { throw 'Vex cursor: native command palette dialog did not open.' }
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), 'Cursor: Toggle Blinking') | Out-Null
    Start-Sleep -Milliseconds 100
    [VexNativeSmoke]::PostMessageW($dialog, 0x111, [IntPtr]1, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 72 0 'real palette command disables cursor animation'
    Assert-CursorIdle $window
    Invoke-NativeAction $window 17
    Assert-NativeProbe $window 72 1 'native dialog action can re-enable cursor animation'

    [VexNativeSmoke]::SendWindowMessage($window, 8, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 76 0 'focus loss disables animation'
    Assert-CursorIdle $window
    Activate-CursorWindow $window
    Assert-NativeProbe $window 75 1 'focus regain restarts animation'
    [VexNativeSmoke]::SendWindowMessage($window, 0x112, [IntPtr]0xf020, [IntPtr]::Zero) | Out-Null
    Assert-CursorIdle $window
    [VexNativeSmoke]::SendWindowMessage($window, 0x112, [IntPtr]0xf120, [IntPtr]::Zero) | Out-Null
    Activate-CursorWindow $window
    Assert-NativeProbe $window 75 1 'restore restarts the eligible cursor'

    Invoke-NativeAction $window 1
    Send-CursorMode $window '[6 q'
    Assert-NativeProbe $window 77 0 'selected split uses a steady cursor'
    Assert-CursorIdle $window
    # Blink resumes only for a focused window; desktops may leave a fresh fixture in the background.
    Activate-CursorWindow $window
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 75 1 'closing the steady split restores its blinking neighbor'

    # A hidden tab's blinking cursor cannot animate a selected tab with a steady cursor.
    Invoke-NativeAction $window 0
    Send-CursorMode $window '[6 q'
    Assert-NativeProbe $window 77 0 'selected tab uses a steady cursor'
    Assert-CursorIdle $window
    Activate-CursorWindow $window
    Invoke-NativeAction $window 5
    Assert-NativeProbe $window 75 1 'returning to the blinking tab restores its timer'
    Invoke-NativeAction $window 7 1
    Send-NativeText $window ("exit 9`r")
    Assert-NativeProbe $window 70 1 'last terminal remains after process exit'
    Assert-CursorIdle $window
    [VexNativeSmoke]::SendWindowMessage($window, 0x113, [IntPtr]3, [IntPtr]::Zero) | Out-Null
    Assert-CursorIdle $window
    $graphicsErrors = [VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]6, [IntPtr]::Zero).ToInt64()
    if ($graphicsErrors -ne 0) { throw "Vex cursor: graphics validation reported $graphicsErrors errors/corruption messages." }
    $graphicsDebug = [bool][VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]9, [IntPtr]::Zero).ToInt64()
    $software = [bool][VexNativeSmoke]::SendWindowMessage($window, 0x8008, [IntPtr]1, [IntPtr]::Zero).ToInt64()
    Close-NativeInstance $process $window
    $process = $null
    $report = [pscustomobject]@{
        Result = 'PASS'
        GraphicsDebug = $graphicsDebug
        Software = $software
        GraphicsErrors = $graphicsErrors
        Checks = @('real VT cursor modes', 'GPU cursor-cell pixel differences for bar/block', 'snapshot reuse', 'input reveals cursor', 'palette toggle', 'focus/minimize/restore', 'inactive-pane and hidden-tab isolation', 'exit and stale timer wake stop animation')
        Limitations = @('synthetic focus/window messages, not physical keyboard or multi-monitor transitions', 'full-scene repaint per eligible blink, not input-to-present latency evidence')
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutputDirectory 'cursor-blink.json')
    $report | ConvertTo-Json -Depth 4
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
}
