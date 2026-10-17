param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\modal-development\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\modals"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\ModalHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
$fixtureSettingsPath = $null
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
function Assert-ThemeCollection([IntPtr]$Dialog, [int]$ControlId, [int]$FirstTheme) {
    $list = [VexNativeSmoke]::GetDlgItem($Dialog, $ControlId)
    $count = [VexNativeSmoke]::SendWindowMessage($list, 0x18B, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    if ($count -ne 6) { throw "Vex theme picker: expected six appearance-matched cards, got $count." }
    for ($i = 0; $i -lt $count; $i++) {
        $value = [VexNativeSmoke]::SendWindowMessage($list, 0x199, [IntPtr]$i, [IntPtr]::Zero).ToInt64()
        if ($value -ne $FirstTheme + $i) { throw 'Vex theme picker: opposite appearance or remapped theme index leaked into the collection.' }
    }
}
try {
    New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
    $OutputDirectory = (Resolve-Path $OutputDirectory).Path
    $env:VEX_NATIVE_SELFTEST = '1'
    $fixtureSettingsPath = Join-Path $OutputDirectory ('settings-' + [Guid]::NewGuid().ToString('N') + '.json')
    $instance = Start-NativeInstance -SettingsPath $fixtureSettingsPath
    $process = $instance.Process; $window = $instance.Window
    $terminalPid = Get-NativeProbe $window 14
    $producer = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("Start-Sleep -Seconds 2;[Console]::WriteLine('VEX_NATIVE_SMOKE_'+'OK')"))
    Send-NativeText $window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -EncodedCommand ' + $producer + "`r")
    Invoke-NativeAction $window 10
    $dialog = Get-OwnedModal $window
    $style = [VexNativeSmoke]::ExtendedStyle($dialog)
    if (($style -band 0x80) -eq 0 -or ($style -band 0x40000) -ne 0) { throw 'Vex native modal: popup incorrectly requests a standalone taskbar window.' }
    Assert-NativeProbe $window 0 1 'real ConPTY output continues while the modal owns input'
    $title = [VexNativeSmoke]::GetDlgItem($dialog, 100)
    if ($title -eq [IntPtr]::Zero) { throw 'Vex modal: selectable heading is missing.' }
    [VexNativeSmoke]::SendWindowMessage($title, 0xB1, [IntPtr]::Zero, [IntPtr](-1)) | Out-Null
    if ([VexNativeSmoke]::SendWindowMessage($title, 0xB0, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() -eq 0) { throw 'Vex modal: heading text cannot be selected.' }
    Assert-DialogCursor $dialog $title 'Text'
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 103)) 'Hand'
    Assert-ThemeCollection $dialog 103 0
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'appearance.png')
    Post-DialogKey $window 0x28
    Assert-NativeProbe $window 51 1 'native theme selection previews the real terminal palette'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Cancel closes the appearance dialog'
    Assert-NativeProbe $window 51 0 'Cancel restores the theme before live preview'
    if (![VexNativeSmoke]::IsWindowEnabled($window)) { throw 'Vex modal: terminal stays disabled after cancellation.' }
    Invoke-NativeAction $window 19
    $dialog = Get-OwnedModal $window
    if ([VexNativeSmoke]::GetDlgItem($dialog, 1) -ne [IntPtr]::Zero -or [VexNativeSmoke]::GetDlgItem($dialog, 2) -ne [IntPtr]::Zero) { throw 'Vex settings: Save/Cancel footer must not exist.' }
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 132)) 'Hand'
    [VexNativeSmoke]::MoveCursorClient($dialog, 24, 500)
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x20, $dialog, [IntPtr]1) | Out-Null
    if (![VexNativeSmoke]::IsArrowCursor()) { throw 'Vex modal cursor: empty settings background must use the arrow.' }
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-general.png')
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]131, [IntPtr]::Zero) | Out-Null
    Assert-ThemeCollection $dialog 115 0
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-appearance.png')
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]132, [IntPtr]::Zero) | Out-Null
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 111)) 'Text'
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 112)) 'Hand'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-terminal.png')
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 111), '99') | Out-Null
    if (![VexNativeSmoke]::IsWindow($dialog) -or (Test-Path $fixtureSettingsPath)) { throw 'Vex modal: invalid font size was accepted or closed the settings dialog.' }
    if (![VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 111), '19')) { throw 'Vex modal: font field is not editable.' }
    if ([VexNativeSmoke]::ControlText([VexNativeSmoke]::GetDlgItem($dialog, 111)) -ne '19') { throw 'Vex modal: font edit did not retain the entered value.' }
    Assert-NativeProbe $window 54 19 'font edits apply immediately without Save'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-edited.png')
    [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 100, 100, 820, 520, 0x0014) | Out-Null
    Start-Sleep -Milliseconds 100
    if ([VexNativeSmoke]::ControlText([VexNativeSmoke]::GetDlgItem($dialog, 111)) -ne '19') { throw 'Vex modal: repositioning discarded the edited font draft.' }
    $ownerBounds = New-Object VexNativeSmoke+Rect; $modalBounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($window, [ref]$ownerBounds) | Out-Null
    [VexNativeSmoke]::GetWindowRect($dialog, [ref]$modalBounds) | Out-Null
    if ($modalBounds.Left -lt $ownerBounds.Left -or $modalBounds.Top -lt $ownerBounds.Top -or $modalBounds.Right -gt $ownerBounds.Right -or $modalBounds.Bottom -gt $ownerBounds.Bottom) { throw 'Vex modal: owned popup escaped its terminal window after resizing.' }
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-compact.png')
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]131, [IntPtr]::Zero) | Out-Null
    Assert-ThemeCollection $dialog 115 0
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-appearance-compact.png')
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]132, [IntPtr]::Zero) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]112, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 72 1 'cursor toggle applies immediately'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Escape closes settings without undoing applied changes'
    Assert-NativeProbe $window 54 19 'settings retain terminal font size'
    Assert-NativeProbe $window 14 $terminalPid 'settings preserve the live ConPTY owner'
    if (!(Test-Path $fixtureSettingsPath)) { throw 'Vex modal: preferences were not saved.' }
    Invoke-NativeAction $window 18
    $dialog = Get-OwnedModal $window
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 141)) 'Hand'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'shell-picker.png')
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]141, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 89 1 'shell picker segmented control selects split right'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'shell picker Escape preserves the live session'
    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window
    $fullPaletteBounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($dialog, [ref]$fullPaletteBounds) | Out-Null
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'command-palette-all.png')
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 102)) 'Text'
    Post-DialogKey $window 0x23
    Assert-NativeProbe $window 100 20 'native palette reaches its last action from the query field'
    if ((Get-NativeProbe $window 102) -le 0) { throw 'Vex modal: native palette failed to scroll to the last action.' }
    Post-DialogKey $window 0x24
    Assert-NativeProbe $window 100 0 'native palette Home restores the first action'
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), 'no-such-vex-command') | Out-Null
    Assert-NativeProbe $window 101 0 'empty search has no actionable result'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'command-palette-empty.png')
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), 'splr') | Out-Null
    $filteredBounds = New-Object VexNativeSmoke+Rect
    [VexNativeSmoke]::GetWindowRect($dialog, [ref]$filteredBounds) | Out-Null
    if ($filteredBounds.Left -ne $fullPaletteBounds.Left -or $filteredBounds.Top -ne $fullPaletteBounds.Top -or $filteredBounds.Right -ne $fullPaletteBounds.Right -or $filteredBounds.Bottom -ne $fullPaletteBounds.Bottom) { throw 'Vex palette: filtering must keep the popup position and size stable.' }
    if ((Get-NativeProbe $window 101) -eq 0) { throw 'Vex modal: fuzzy command query lost its results.' }
    Assert-DialogCursor $dialog ([VexNativeSmoke]::GetDlgItem($dialog, 103)) 'Hand'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'command-palette.png')
    $commands = [VexNativeSmoke]::GetDlgItem($dialog, 103)
    Send-NativePointer $commands 0x0201 20 14 1
    Send-NativePointer $commands 0x0202 20 14 0
    Assert-NativeProbe $window 11 2 'one click executes the native command palette split action'
    Send-NativeStatus $window 'state=working:app=rex-ui-fixture'
    Assert-NativeProbe $window 31 2 'close fixture reports running work'
    Invoke-NativeAction $window 16 99
    $dialog = Get-OwnedModal $window
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'close-confirmation.png')
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 11 2 'cancelling close retains both live panes'
    Invoke-NativeAction $window 0
    Invoke-NativeAction $window 11
    $dialog = Get-OwnedModal $window
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'tab-overview.png')
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]2, [IntPtr]::Zero) | Out-Null
    Close-NativeInstance $process $window
    $instance = Start-NativeInstance -SettingsPath $fixtureSettingsPath
    $process = $instance.Process; $window = $instance.Window
    Assert-NativeProbe $window 54 19 'font size survives restart'
    Invoke-NativeAction $window 19
    $dialog = Get-OwnedModal $window
    $focusBefore = [VexNativeSmoke]::ThreadFocus($window)
    Post-DialogKey $window 9
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do { Start-Sleep -Milliseconds 20; $focusAfter = [VexNativeSmoke]::ThreadFocus($window) } while ($focusAfter -eq $focusBefore -and [DateTime]::UtcNow -lt $deadline)
    if ($focusAfter -eq $focusBefore -or ![VexNativeSmoke]::IsChild($dialog, $focusAfter)) { throw 'Vex modal: Tab did not move focus inside the dialog.' }
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Escape dismisses the native settings dialog'
    [IO.File]::SetAttributes($fixtureSettingsPath, [IO.FileAttributes]::ReadOnly)
    $savedBytes = [IO.File]::ReadAllText($fixtureSettingsPath)
    Invoke-NativeAction $window 19
    $dialog = Get-OwnedModal $window
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]132, [IntPtr]::Zero) | Out-Null
    $fontField = [VexNativeSmoke]::GetDlgItem($dialog, 111)
    Send-NativePointer $fontField 0x0201 12 12 1
    Send-NativePointer $fontField 0x0202 12 12 0
    [VexNativeSmoke]::SendWindowMessage($fontField, 0xB1, [IntPtr]::Zero, [IntPtr](-1)) | Out-Null
    [VexNativeSmoke]::PostMessageW($fontField, 0x102, [IntPtr]50, [IntPtr]::Zero) | Out-Null
    [VexNativeSmoke]::PostMessageW($fontField, 0x102, [IntPtr]48, [IntPtr]::Zero) | Out-Null
    $warning = Get-OwnedModal $dialog 'Vex settings' -RootOwner $window
    if ([VexNativeSmoke]::ControlText($warning) -ne 'Vex settings') { throw 'Vex modal: failed preference write was not reported.' }
    Save-DialogImage $warning (Join-Path $OutputDirectory 'settings-warning.png')
    $dismiss = [VexNativeSmoke]::GetDlgItem($warning, 1)
    if ($dismiss -eq [IntPtr]::Zero) { $dismiss = [VexNativeSmoke]::GetDlgItem($warning, 2) }
    if ($dismiss -eq [IntPtr]::Zero) { throw 'Vex settings: warning dismiss button is missing.' }
    [VexNativeSmoke]::PostMessageW($dismiss, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 54 19 'failed save leaves runtime preferences unchanged'
    if ([IO.File]::ReadAllText($fixtureSettingsPath) -ne $savedBytes) { throw 'Vex modal: failed save damaged prior preferences.' }
    [IO.File]::SetAttributes($fixtureSettingsPath, [IO.FileAttributes]::Normal)
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([VexNativeSmoke]::ControlText($fontField) -ne '19' -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }
    if ([VexNativeSmoke]::ControlText($fontField) -ne '19') { throw "Vex settings: failed live save did not restore the applied font value (field=$([VexNativeSmoke]::ControlText($fontField)) runtime=$(Get-NativeProbe $window 54) overlay=$(Get-NativeProbe $window 53))." }
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'settings remain usable after failed live save'
    Close-NativeInstance $process $window
    $process = $null
    $lightSettings = [IO.File]::ReadAllText($fixtureSettingsPath) | ConvertFrom-Json
    $lightSettings.theme = 'Vex Alabaster'
    [IO.File]::WriteAllText($fixtureSettingsPath, ($lightSettings | ConvertTo-Json))
    $instance = Start-NativeInstance -SettingsPath $fixtureSettingsPath
    $process = $instance.Process; $window = $instance.Window
    Assert-NativeProbe $window 51 6 'light modal fixture uses the light terminal theme'
    Invoke-NativeAction $window 19
    $dialog = Get-OwnedModal $window
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-light.png')
    $shellCombo = [VexNativeSmoke]::GetDlgItem($dialog, 113)
    $shellRows = [VexNativeSmoke]::SendWindowMessage($shellCombo, 0x146, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64()
    $shellRow = -1
    for ($row = 0; $row -lt $shellRows; $row++) {
        if ([VexNativeSmoke]::SendWindowMessage($shellCombo, 0x150, [IntPtr]$row, [IntPtr]::Zero).ToInt64() -eq 2) { $shellRow = $row; break }
    }
    if ($shellRow -lt 0) { throw 'Vex settings: Windows PowerShell is missing from default-shell choices.' }
    [VexNativeSmoke]::SendWindowMessage($shellCombo, 0x14E, [IntPtr]$shellRow, [IntPtr]::Zero) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr](0x10000 + 113), $shellCombo) | Out-Null
    Assert-NativeProbe $window 34 2 'default shell applies immediately without Save'
    $liveSettings = [IO.File]::ReadAllText($fixtureSettingsPath) | ConvertFrom-Json
    if ($liveSettings.default_shell -ne 'powershell') { throw 'Vex settings: default shell was not saved automatically.' }
    [VexNativeSmoke]::SendWindowMessage($dialog, 0x111, [IntPtr]131, [IntPtr]::Zero) | Out-Null
    Assert-ThemeCollection $dialog 115 6
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'settings-appearance-light.png')
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'light settings cancel restores terminal input'
    Invoke-NativeAction $window 10
    $dialog = Get-OwnedModal $window
    Assert-ThemeCollection $dialog 103 6
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'appearance-light.png')
    Post-DialogKey $window 0x28
    Assert-NativeProbe $window 51 7 'light card retains its global palette index during preview'
    Assert-NativeProbe $window 52 0 'light preview does not change application appearance'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 51 6 'light Cancel restores the original palette'
    Invoke-NativeAction $window 10
    $dialog = Get-OwnedModal $window
    Post-DialogKey $window 0x28
    Assert-NativeProbe $window 51 7 'light palette can be previewed again'
    Post-DialogKey $window 0x0D
    Assert-NativeProbe $window 53 0 'Enter applies and closes Theme Studio'
    $savedTheme = [IO.File]::ReadAllText($fixtureSettingsPath) | ConvertFrom-Json
    if ($savedTheme.theme -ne 'Vex Linen') { throw 'Vex theme picker: Apply did not save the selected custom palette.' }
    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'command-palette-light.png')
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'light palette Escape restores terminal input'
    Close-NativeInstance $process $window
    $process = $null
    @{ Result = 'PASS'; Checks = @('owned popup', 'modal input isolation', 'selectable title', 'cancel restores input', 'settings apply automatically without Save/Cancel', 'atomic persistence and live-save rollback', 'restart restoration', 'stable ConPTY', 'segmented shell modes', 'palette stays stable while filtering', 'empty search', 'one-click commands', 'close cancellation', 'dark and light modal screenshots', 'appearance-filtered theme collections', 'light theme global indices', 'theme preview rollback', 'theme Apply persistence', 'compact theme cards') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'modals.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    if ($fixtureSettingsPath -and (Test-Path $fixtureSettingsPath)) { [IO.File]::SetAttributes($fixtureSettingsPath, [IO.FileAttributes]::Normal) }
    if ($previousDpi -ne [IntPtr]::Zero) { [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
