param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'

function Get-NativeTabBodyX([IntPtr]$Window, [int]$Index) {
    $width = (Get-NativeProbe $Window 48) / 1000.0
    return 140 + $Index * ($width + 4) + [Math]::Min(40, ($width - 22) / 2)
}
function Assert-NativeTabOrder([IntPtr]$Window, [long[]]$Ids) {
    Assert-NativeProbe $Window 10 $Ids.Length 'tab count'
    for ($index = 0; $index -lt $Ids.Length; $index++) { Assert-NativeProbe $Window (60 + $index) $Ids[$index] 'stable tab order' }
}
function Assert-NativeTabGesture($Result, [long]$SourceId, [int]$Moved = 2) {
    if ($Result.Pressed -ne 1 -or $Result.CapturedPress -or $Result.Moved -ne $Moved -or $Result.CapturedMove -ne ($Moved -eq 2) -or $Result.SourceIdBeforeDrop -ne $SourceId) {
        throw "Vex native tabs: incorrect gesture/capture/preview ownership ($($Result | ConvertTo-Json -Compress))."
    }
}
function Assert-NativeTabDragEnded([IntPtr]$Window) {
    Assert-NativeProbe $Window 45 0 'tab drag cleared'
    Assert-NativeProbe $Window 42 0 'tab capture released'
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
try {
    $previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Invoke-NativeAction $window 1
    $paneA = Get-NativeProbe $window 12
    $pidA = Get-NativeProbe $window 14
    Send-NativeText $window ('@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%B' + "`r")
    Assert-NativeProbe $window 3 1 'split pane A marker'
    Invoke-NativeAction $window 0
    $pidB = Get-NativeProbe $window 14
    Send-NativeText $window ('@set VEX_TAB=VEX_NATIVE_TAB_' + "`r" + '@echo %VEX_TAB%B' + "`r")
    Assert-NativeProbe $window 5 1 'tab B marker'
    Invoke-NativeAction $window 0
    Assert-NativeTabOrder $window @(1, 3, 4)

    $result = Invoke-NativeTabGesture $window 1 ((Get-NativeTabBodyX $window 1) + 3)
    Assert-NativeTabGesture $result 3 1
    Assert-NativeTabDragEnded $window
    Assert-NativeProbe $window 46 3 'plain press selects tab B'
    Assert-NativeProbe $window 14 $pidB 'click does not restart tab B'
    Assert-NativeTabOrder $window @(1, 3, 4)

    $result = Invoke-NativeTabGesture $window 0 3000
    Assert-NativeTabGesture $result 1
    if ($result.Destination -ne 3) { throw 'Vex native tabs: missing last-slot marker.' }
    Assert-NativeTabDragEnded $window
    Assert-NativeTabOrder $window @(3, 4, 1)
    Assert-NativeProbe $window 46 1 'selection follows dragged tab A'
    Assert-NativeProbe $window 47 2 'selected index moves right'
    Assert-NativeProbe $window 12 $paneA 'split focus survives reorder'
    Assert-NativeProbe $window 14 $pidA 'PTY process survives reorder'
    Assert-NativeProbe $window 11 2 'split tree survives reorder'
    Assert-NativeProbe $window 13 4 'no sessions added or removed'
    Assert-NativeProbe $window 3 1 'parsed viewport survives reorder'
    Assert-NativeTabGesture (Invoke-NativeTabGesture $window 2 -300) 1
    Assert-NativeTabOrder $window @(1, 3, 4)
    Assert-NativeProbe $window 47 0 'selected index moves left'

    $outside = Invoke-NativeTabGesture $window 0 3000 100
    Assert-NativeTabGesture $outside 1
    if ($outside.Destination -ne 0) { throw 'Vex native tabs: unexpected drop target below strip.' }
    Assert-NativeTabOrder $window @(1, 3, 4)
    # Cancel modes release over chrome inside the replay: Escape, capture loss, cancel mode, focus, DPI and tab change.
    foreach ($operation in @(1, 2, 3, 4, 5, 8)) {
        Assert-NativeTabGesture (Invoke-NativeTabGesture $window 1 3000 20 $operation) 3
        Assert-NativeTabDragEnded $window
        Assert-NativeTabOrder $window @(1, 3, 4)
    }
    Invoke-NativeAction $window 6 1
    Assert-NativeProbe $window 5 1 'tab B still owns original output'
    Send-NativeStatus $window 'state=done:app=build'
    Assert-NativeProbe $window 31 3 'unread result before drag'
    Assert-NativeTabGesture (Invoke-NativeTabGesture $window 1 3000) 3
    Assert-NativeTabOrder $window @(1, 4, 3)
    Assert-NativeProbe $window 31 3 'drag does not acknowledge unread program status'
    Assert-NativeProbe $window 14 $pidB 'tab B process identity remains stable'

    Assert-NativeTabGesture (Invoke-NativeTabGesture $window 0 3000 20 6) 1
    Assert-NativeTabDragEnded $window
    Assert-NativeTabOrder $window @(4, 3)
    Assert-NativeProbe $window 46 4 'close chooses reordered right neighbor for index zero'
    Assert-NativeProbe $window 13 2 'closing source destroys its two panes only'
    Send-NativePointer $window 0x200 -300 20
    Assert-NativeTabOrder $window @(4, 3)
    for ($index = 0; $index -lt 6; $index++) { Invoke-NativeAction $window 0 }
    Assert-NativeProbe $window 10 8 'eight-tab compact geometry'
    $lastId = Get-NativeProbe $window 67
    $lastPid = Get-NativeProbe $window 14
    Assert-NativeTabGesture (Invoke-NativeTabGesture $window 7 -300) $lastId
    Assert-NativeProbe $window 60 $lastId 'compact last-to-first drop'
    Assert-NativeProbe $window 14 $lastPid 'compact drag preserves PTY'
    Assert-NativeProbe $window 13 8 'compact drag preserves session count'
    Assert-NativeProbe $window 51 0 'default theme is Vex Obsidian'
    Assert-NativeProbe $window 52 1 'default theme is dark'
    Invoke-NativeAction $window 10 99
    Assert-NativeProbe $window 51 6 'toggle switches to default light theme Vex Alabaster'
    Assert-NativeProbe $window 52 0 'light theme sets is_dark to 0'
    Invoke-NativeAction $window 10 9
    Assert-NativeProbe $window 51 8 'applyTheme selects Tokyo Night Day'
    Assert-NativeProbe $window 52 0 'Tokyo Night Day is light'
    Invoke-NativeAction $window 10 99
    Assert-NativeProbe $window 51 0 'toggle returns to last dark theme'
    Assert-NativeProbe $window 52 1 'dark mode restored'
    Invoke-NativeAction $window 10 99
    Assert-NativeProbe $window 51 8 'toggle remembers last selected light theme'
    Invoke-NativeAction $window 10 2
    Assert-NativeProbe $window 51 1 'applyTheme selects Tokyo Night Storm'
    Assert-NativeProbe $window 52 1 'Tokyo Night Storm is dark'
    Invoke-NativeAction $window 9 0
    Assert-NativeProbe $window 53 1 'command palette overlay opens'
    Invoke-NativeAction $window 10 0
    Assert-NativeProbe $window 53 2 'theme studio overlay opens'
    Invoke-NativeAction $window 11 0
    Assert-NativeProbe $window 53 3 'tab peek overlay opens'
    Invoke-NativeAction $window 9 1
    Assert-NativeProbe $window 53 0 'overlay closes cleanly'
    Assert-NativeProbe $window 54 14 'default font size is 14pt'
    Invoke-NativeAction $window 13 0
    Assert-NativeProbe $window 54 15 'zoom in increases font size to 15pt'
    Invoke-NativeAction $window 14 0
    Invoke-NativeAction $window 14 0
    Assert-NativeProbe $window 54 13 'zoom out decreases font size to 13pt'
    Invoke-NativeAction $window 15 0
    Assert-NativeProbe $window 54 14 'zoom reset restores font size to 14pt'
    Invoke-NativeAction $window 12 0
    Assert-NativeProbe $window 53 4 'path completion overlay opens'
    if ((Get-NativeProbe $window 55) -le 0) { throw 'Vex native tabs: path completion returned zero matches.' }
    Invoke-NativeAction $window 12 99
    Assert-NativeProbe $window 53 0 'path completion overlay closes cleanly'
    Send-NativeText $window ('@set VEX_FLOOD=VEX_NATIVE_FLOOD_' + "`r" + '@for /l %i in (1,1,1000000) do @echo %VEX_FLOOD%OK' + "`r")
    Assert-NativeProbe $window 1 1 'flooding output reaches the real viewport before captured shutdown'
    Assert-NativeTabGesture (Invoke-NativeTabGesture $window 0 3000 20 7) $lastId
    if (!$process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Vex native tabs: flooding captured shutdown failed.' }
    $process = $null
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    @{ result = 'PASS'; checks = 'atomic Win32 replay, threshold, selection, both move directions, stable tab/pane/PTY/viewport/status owners, outside drop, cancellation/DPI, source close, eight tabs, dark/light themes, overlays, font zoom, path completion and flooding captured shutdown' } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'tabs.json')
    Write-Host 'Vex native tabs: PASS'
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    if ($previousDpiContext -and $previousDpiContext -ne [IntPtr]::Zero) { [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null }
}
