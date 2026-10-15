param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--shell cmd'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

function Send-PowerShellStatus([IntPtr]$Window, [string]$Body, [int]$DelayMs = 0, [int]$HoldSeconds = 0) {
    $prefix = if ($DelayMs -gt 0) { "Start-Sleep -Milliseconds $DelayMs; " } else { "" }
    $suffix = if ($HoldSeconds -gt 0) { "; Start-Sleep -Seconds $HoldSeconds" } else { "" }
    $cmd = "$prefix[Console]::Write([char]27 + ']7501;$Body' + [char]7)$suffix`r"
    Send-NativeText $Window $cmd
}

$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldCapture = $env:VEX_NATIVE_CAPTURE_BMP
$previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.capture.bmp'
    $instance = Start-NativeInstance -Arguments $NativeLaunchArguments
    $process = $instance.Process
    $window = $instance.Window

    Assert-NativeProbe $window 34 1 'initial default shell is cmd (1)'
    Assert-NativeProbe $window 85 1 'initial pane shell is cmd (1)'
    $availableCount = Get-NativeProbe $window 88
    if ($availableCount -lt 2) {
        throw "Vex shell picker: expected at least 2 discovered shells on Windows, got $availableCount."
    }
    $pane1Id = Get-NativeProbe $window 12

    # 1. Open Shell Picker overlay (action 18, index 0)
    Invoke-NativeAction $window 18 0
    Assert-NativeProbe $window 53 6 'shell picker overlay opens'
    Assert-NativeProbe $window 89 0 'shell picker starts in New Tab mode (0)'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-shell-picker.png')

    # Close Shell Picker overlay (action 18, index 99)
    Invoke-NativeAction $window 18 99
    Assert-NativeProbe $window 53 0 'shell picker overlay closes'

    # 2. Split right with explicit Windows PowerShell override (action 18, index 12 -> mode=split_right, kind=2=powershell)
    Invoke-NativeAction $window 18 12
    Assert-NativeProbe $window 11 2 'split_right with shell override creates a second pane'
    Assert-NativeProbe $window 85 2 'new split pane uses Windows PowerShell (2)'
    Assert-NativeProbe $window 34 1 'default shell remains cmd (1) after one-off split override'
    $pane2Id = Get-NativeProbe $window 12
    if ($pane2Id -eq $pane1Id) {
        throw 'Vex shell picker: split pane did not receive focus.'
    }

    # Wait for PowerShell pane to become ready
    Send-NativeText $window "Write-Host VEX_NATIVE_PANE_B`r"
    Assert-NativeProbe $window 3 1 'PowerShell split pane is interactive'

    # 3. Switch default shell to Windows PowerShell (action 18, index 32 -> mode=set_default, kind=2=powershell)
    Invoke-NativeAction $window 18 32
    Assert-NativeProbe $window 34 2 'default shell updates to Windows PowerShell (2)'

    # 4. Open a new tab with the new default shell (action 0 -> new_tab)
    Invoke-NativeAction $window 0 0
    Assert-NativeProbe $window 10 2 'new tab created'
    Assert-NativeProbe $window 47 1 'new tab is selected (index 1)'
    Assert-NativeProbe $window 85 2 'new tab inherits updated default shell (Windows PowerShell = 2)'

    # 5. Switch back to Tab 0 (action 6 -> select_tab) and test multi-pane status origin in Pane 2
    Invoke-NativeAction $window 6 0
    Assert-NativeProbe $window 47 0 'switched back to Tab 0'
    Assert-NativeProbe $window 12 $pane2Id 'Tab 0 remembers Pane 2 as active'

    Send-PowerShellStatus $window 'state=done:app=Cargo:title=Q29tcGlsaW5nIHZleC1uYXRpdmU='
    Assert-NativeProbe $window 31 3 'active pane status state is done (3)'
    Assert-NativeProbe $window 32 3 'tab status state is done (3)'
    Assert-NativeProbe $window 86 2 'tab status origin_pane_number is 2 (second leaf in split tree)'
    Assert-NativeProbe $window 87 $pane2Id 'tab status origin_pane_id matches Pane 2'

    # Open Tab Peek (action 11, index 0) to render status detail + "• Pane 2" origin badge
    Invoke-NativeAction $window 11 0
    Assert-NativeProbe $window 53 3 'tab peek overlay opens with multi-pane status origin'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-tab-peek-origin.png')
    Invoke-NativeAction $window 11 1
    Assert-NativeProbe $window 53 0 'tab peek overlay closes'

    # Clear status in Pane 2 before testing background attention routing
    Send-PowerShellStatus $window 'state=clear'
    Assert-NativeProbe $window 86 0 'tab status origin cleared before attention routing test'

    # 6. Schedule a delayed blocked status in Pane 2, focus Pane 1 in Tab 0, switch to Tab 1, and verify attention-origin focus routing
    Send-PowerShellStatus $window 'state=blocked:kind=permission:app=Deploy:title=QXBwcm92ZSBwcm9kdWN0aW9uIHJlbGVhc2U=' 500 30
    # Click left pane (Pane 1) at (120, 180) so Tab 0's active_pane is Pane 1 while Pane 2 is in the background
    Send-NativePointer $window 0x0201 120 180 1
    Send-NativePointer $window 0x0202 120 180 0
    Assert-NativeProbe $window 12 $pane1Id 'Pane 1 is focused in Tab 0 before switching away'
    Assert-NativeProbe $window 85 1 'Pane 1 retains cmd shell kind (1)'

    # Immediately switch to Tab 1 (action 6, index 1) before the 500ms delayed status fires in Tab 0's Pane 2
    Invoke-NativeAction $window 6 1
    Assert-NativeProbe $window 47 1 'Tab 1 is active while Tab 0 Pane 2 emits blocked status'

    # Wait for workspace-wide highest status (probe 33) to become blocked (4)
    Assert-NativeProbe $window 33 4 'background Tab 0 Pane 2 transitions to blocked status (4)'

    # Select Tab 0 (action 6, index 0) and verify focusTabAttentionOrigin routes focus to Pane 2!
    Invoke-NativeAction $window 6 0
    Assert-NativeProbe $window 47 0 'Tab 0 selected'
    Assert-NativeProbe $window 12 $pane2Id 'selecting Tab 0 automatically focuses attention-origin Pane 2'
    Assert-NativeProbe $window 86 2 'Tab 0 status origin_pane_number remains 2'
    Assert-NativeProbe $window 87 $pane2Id 'Tab 0 status origin_pane_id remains Pane 2'

    Write-Host 'PASS: Native Shell Picker, per-pane shell overrides, default shell switching, status origin, and attention-origin focus routing verified.'
} finally {
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:VEX_NATIVE_CAPTURE_BMP = $oldCapture
    if ($previousDpiContext -ne [IntPtr]::Zero) {
        [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null
    }
    if ($process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}
