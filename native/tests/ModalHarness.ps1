. "$PSScriptRoot\DesktopHarness.ps1"

function Get-OwnedModal([IntPtr]$Owner, [string]$ExpectedTitle = '', [IntPtr]$RootOwner = $Owner) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $dialog = [VexNativeSmoke]::GetLastActivePopup($RootOwner)
        if ($dialog -ne $Owner -and $dialog -ne [IntPtr]::Zero) {
            if ([VexNativeSmoke]::GetWindow($dialog, 4) -ne $Owner) { throw 'Vex native modal: dialog has the wrong owner.' }
            if ([VexNativeSmoke]::IsWindowEnabled($Owner)) { throw 'Vex native modal: terminal input is not blocked.' }
            if (!$ExpectedTitle -or [VexNativeSmoke]::ControlText($dialog) -eq $ExpectedTitle) { return $dialog }
        }
        Start-Sleep -Milliseconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Vex native modal: owned popup did not open.'
}
function Assert-DialogCursor([IntPtr]$Dialog, [IntPtr]$Control, [ValidateSet('Arrow','Text','Hand')][string]$Expected) {
    # Cursor updates are asynchronous: resend the pointer and WM_SETCURSOR until the expected shape appears or the deadline passes.
    [VexNativeSmoke]::SetForegroundWindow($Dialog) | Out-Null
    $scale = (Get-NativeSurface $Control).Scale
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        [VexNativeSmoke]::MoveCursorClient($Control, 12, 12)
        Send-NativePointer $Control 0x0200 (12 / $scale) (12 / $scale)
        [VexNativeSmoke]::SendWindowMessage($Dialog, 0x20, $Control, [IntPtr]1) | Out-Null
        $matches = switch ($Expected) { 'Arrow' { [VexNativeSmoke]::IsArrowCursor() } 'Text' { [VexNativeSmoke]::IsTextCursor() } 'Hand' { [VexNativeSmoke]::IsHandCursor() } }
        if ($matches) { return }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ("Vex modal cursor: expected $Expected for " + [VexNativeSmoke]::ControlClass($Control) + " arrow=" + [VexNativeSmoke]::IsArrowCursor() + " hand=" + [VexNativeSmoke]::IsHandCursor() + " text=" + [VexNativeSmoke]::IsTextCursor())
}
function Post-DialogKey([IntPtr]$Owner, [int]$Key) {
    # Windows moves focus into a newly shown modal asynchronously; wait for that before posting keys.
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $dialog = [VexNativeSmoke]::GetLastActivePopup($Owner)
        $focus = [VexNativeSmoke]::ThreadFocus($Owner)
        if ($dialog -ne $Owner -and $focus -ne [IntPtr]::Zero -and [VexNativeSmoke]::IsChild($dialog, $focus)) {
            if (![VexNativeSmoke]::PostMessageW($focus, 0x100, [IntPtr]$Key, [IntPtr]::Zero)) { throw 'Vex native modal: keyboard event could not be posted.' }
            return
        }
        Start-Sleep -Milliseconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Vex native modal: focus did not enter the owned dialog.'
}
function Save-DialogImage([IntPtr]$Dialog, [string]$Path) {
    $bounds = New-Object VexNativeSmoke+Rect
    if (![VexNativeSmoke]::GetWindowRect($Dialog, [ref]$bounds)) { throw 'Vex modal: no window bounds.' }
    $image = New-Object Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
    $graphics = [Drawing.Graphics]::FromImage($image)
    $dc = $graphics.GetHdc()
    try { if (![VexNativeSmoke]::PrintWindow($Dialog, $dc, 0)) { throw 'Vex modal: native control capture failed.' } }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { $image.Save($Path, [Drawing.Imaging.ImageFormat]::Png) }
    finally { $image.Dispose() }
}
