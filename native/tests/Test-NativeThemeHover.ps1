param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\theme-hover"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\ModalHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
function Assert-DialogHoverDoesNotPaint([IntPtr]$Dialog, [int]$ControlId) {
    $paints = [VexNativeSmoke]::SendWindowMessage($Dialog, 0x8003, [IntPtr]$ControlId, [IntPtr]::Zero).ToInt64()
    if ($paints -ne 0) { throw "Vex modal hover: control $ControlId triggered $paints paints without a click (or is not visible)." }
    $restored = [VexNativeSmoke]::SendWindowMessage($Dialog, 0x8005, [IntPtr]$ControlId, [IntPtr]::Zero).ToInt64()
    if ($restored -ne 1) { throw "Vex modal drawing: control $ControlId leaked GDI font, text color or background state into native painting." }
}
try {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path $OutputDirectory).Path
    $env:VEX_NATIVE_SELFTEST = '1'
    $settingsPath = Join-Path $OutputDirectory 'settings.json'
    $results = @()
    foreach ($theme in @('Vex Obsidian', 'Vex Alabaster')) {
        [IO.File]::WriteAllText($settingsPath, (@{ theme = $theme } | ConvertTo-Json))
        $instance = Start-NativeInstance -SettingsPath $settingsPath
        $process = $instance.Process; $window = $instance.Window
        $initialTheme = if ($theme -eq 'Vex Obsidian') { 0 } else { 6 }
        foreach ($kind in @(10, 19, 9, 18, 11)) {
            Invoke-NativeAction $window $kind
            $dialog = Get-OwnedModal $window
            if ($kind -eq 19) {
                Assert-DialogHoverDoesNotPaint $dialog 113
                [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]132, [IntPtr]::Zero) | Out-Null
                Assert-DialogHoverDoesNotPaint $dialog 112
                [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]131, [IntPtr]::Zero) | Out-Null
                foreach ($id in @(130, 131, 132, 133, 125)) { Assert-DialogHoverDoesNotPaint $dialog $id }
            }
            if ($kind -eq 18) {
                foreach ($id in @(140, 141, 142, 143)) { Assert-DialogHoverDoesNotPaint $dialog $id }
            }
            $listId = if ($kind -eq 19) { 115 } else { 103 }
            Assert-DialogHoverDoesNotPaint $dialog $listId
            Assert-NativeProbe $window 51 $initialTheme 'hover never changes the selected theme'
            if ($kind -eq 10 -or $kind -eq 19) {
                $list = [VexNativeSmoke]::GetDlgItem($dialog, $listId)
                $columnWidth = (Get-NativeSurface $list).Width / 3
                Send-NativePointer $list 0x0201 ($columnWidth + 20) 20 1
                Send-NativePointer $list 0x0202 ($columnWidth + 20) 20 0
                if ($kind -eq 10) {
                    Assert-NativeProbe $window 51 ($initialTheme + 2) 'click previews the selected theme'
                } else {
                    $selected = [VexNativeSmoke]::SendWindowMessage($list, 0x188, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
                    if ($selected -ne 2) { throw 'Vex theme hover: click did not select the settings card.' }
                    $initialTheme += 2
                    Assert-NativeProbe $window 51 $initialTheme 'settings theme click applies immediately'
                    $saved = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
                    $expectedName = if ($theme -eq 'Vex Obsidian') { 'Vex Grove' } else { 'Vex Mist' }
                    if ($saved.theme -ne $expectedName) { throw 'Vex live theme: selected palette was not persisted automatically.' }
                }
                Save-DialogImage $dialog (Join-Path $OutputDirectory ("$theme-$kind.png"))
            }
            $results += @{ Theme = $theme; Dialog = $kind; HoverPaints = 0 }
            Post-DialogKey $window 0x1B
            Assert-NativeProbe $window 53 0 'static modal fixture closes the picker'
            Assert-NativeProbe $window 51 $initialTheme 'closing settings keeps applied changes; cancelling preview restores the theme'
        }
        Close-NativeInstance $process $window
        $process = $null
        $instance = Start-NativeInstance -SettingsPath $settingsPath
        $process = $instance.Process; $window = $instance.Window
        Assert-NativeProbe $window 51 $initialTheme 'live settings theme survives restart'
        Close-NativeInstance $process $window
        $process = $null
    }
    @{ Result = 'PASS'; Cases = $results; Checks = @('no hover paint on cards, rows, navigation, buttons, toggle or combo', 'owner-draw GDI state is preserved', 'click selects and saves the settings theme immediately', 'closing settings retains applied themes'); Limitations = @('atomic synthetic hover replay through real Win32 controls; not frame-time or physical-pointer measurements') } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'theme-hover.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
