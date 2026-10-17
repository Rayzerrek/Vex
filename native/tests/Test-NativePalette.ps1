param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\palette"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\ModalHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$previousSelftest = $env:VEX_NATIVE_SELFTEST
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $pidBefore = Get-NativeProbe $window 14

    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window 'Command Palette'
    $count = Get-NativeProbe $window 101
    if ($count -lt 20) { throw "Vex palette: expected the full command list, got $count commands." }
    Post-DialogKey $window 0x26
    Assert-NativeProbe $window 100 ($count - 1) 'Up wraps from the first command to the last, beyond the first nine rows'
    Post-DialogKey $window 0x24
    Assert-NativeProbe $window 100 0 'Home returns to the first command'
    Post-DialogKey $window 0x23
    Assert-NativeProbe $window 100 ($count - 1) 'End reaches the last command'
    Post-DialogKey $window 0x0D
    Assert-NativeProbe $window 53 7 'Enter on the last command (Settings) opens the native settings dialog'
    Assert-NativeProbe $window 14 $pidBefore 'palette commands do not recreate the PTY'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Escape closes the settings dialog'

    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window 'Command Palette'
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), 'splr') | Out-Null
    Assert-NativeProbe $window 103 4 'fuzzy query text reaches the terminal owner'
    if ((Get-NativeProbe $window 101) -eq 0) { throw 'Vex palette: fuzzy query has no matches.' }
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-fuzzy.png')
    $list = [VexNativeSmoke]::GetDlgItem($dialog, 103)
    Send-NativePointer $list 0x0201 20 300 1
    Send-NativePointer $list 0x0202 20 300 0
    Assert-NativeProbe $window 53 1 'blank result space never executes the nearest command'
    $query = [VexNativeSmoke]::GetDlgItem($dialog, 102)
    [VexNativeSmoke]::SetControlText($query, 'split right') | Out-Null
    [VexNativeSmoke]::SendWindowMessage($query, 0xB1, [IntPtr]11, [IntPtr]11) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($query, 0x102, [IntPtr]127, [IntPtr]::Zero) | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split ') { throw 'Vex palette: Ctrl+Backspace inserted DEL or failed to delete the previous query word.' }
    Assert-NativeProbe $window 103 6 'Ctrl+Backspace leaves a valid query'
    [VexNativeSmoke]::SendWindowMessage($query, 0x304, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split right') { throw 'Vex palette: word deletion broke native Undo.' }
    [VexNativeSmoke]::SendWindowMessage($query, 0xB1, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($query, 0x102, [IntPtr]127, [IntPtr]::Zero) | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split right') { throw 'Vex palette: Ctrl+Backspace at the start changed the query.' }
    [VexNativeSmoke]::SendWindowMessage($query, 0xB1, [IntPtr]6, [IntPtr]11) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($query, 0x102, [IntPtr]127, [IntPtr]::Zero) | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split ') { throw 'Vex palette: Ctrl+Backspace did not remove the selected text.' }
    [VexNativeSmoke]::SetControlText($query, ('split ' + [char]::ConvertFromUtf32(0x1F600))) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($query, 0xB1, [IntPtr]8, [IntPtr]8) | Out-Null
    [VexNativeSmoke]::SendWindowMessage($query, 0x102, [IntPtr]127, [IntPtr]::Zero) | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split ') { throw 'Vex palette: word deletion split a Unicode surrogate pair.' }
    Assert-NativeProbe $window 14 $pidBefore 'query editing preserves the live PTY'
    [VexNativeSmoke]::SetControlText($query, ('split' + [char]1 + ' right' + [char]127)) | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split right') { throw 'Vex palette: control characters were not removed at the native input boundary.' }
    [VexNativeSmoke]::SetControlText($query, "split`r`nright`t") | Out-Null
    if ([VexNativeSmoke]::ControlText($query) -ne 'split right ') { throw 'Vex palette: pasted query whitespace was not normalized.' }
    Assert-NativeProbe $window 103 12 'normalized pasted query reaches the owner'
    [VexNativeSmoke]::SetControlText([VexNativeSmoke]::GetDlgItem($dialog, 102), ('x' * 126) + [char]::ConvertFromUtf32(0x1F600)) | Out-Null
    Assert-NativeProbe $window 103 126 'query limit keeps complete UTF-8 characters'
    Post-DialogKey $window 0x1B
    Assert-NativeProbe $window 53 0 'Escape closes the palette'

    Invoke-NativeAction $window 10 7
    Invoke-NativeAction $window 9
    $dialog = Get-OwnedModal $window 'Command Palette'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-light.png')
    [VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 640, 400, 0x0040) | Out-Null
    Start-Sleep -Milliseconds 100
    $dialog = Get-OwnedModal $window 'Command Palette'
    Save-DialogImage $dialog (Join-Path $OutputDirectory 'palette-compact.png')
    Post-DialogKey $window 0x1B
    Close-NativeInstance $process $window
    $process = $null
    [pscustomobject]@{ Result = 'PASS'; Checks = @('every action reachable with keyboard', 'Up/Home/End wrap and scroll', 'native fuzzy query', 'Ctrl+Backspace word/selection deletion and Unicode', 'native Undo after word deletion', 'control character filtering', 'pasted whitespace normalization', 'UTF-8 query limit', 'stable PTY', 'dark/light/compact native captures') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $previousSelftest
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
}
