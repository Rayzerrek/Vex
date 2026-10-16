param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
# Raw custom cmd isolates OSC 7501 tests from the integrated shell's prompt lifecycle.
$NativeLaunchArguments = '--program "' + $env:WINDIR + '\System32\cmd.exe" --arguments /d'

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$metrics = Join-Path $OutputDirectory 'startup.txt'
Remove-Item $metrics -ErrorAction SilentlyContinue
$oldMetrics = $env:VEX_NATIVE_METRICS
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$process = $null
$backgroundGate = $null
try {
    $previousDpiContext = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
    $env:VEX_NATIVE_METRICS = $metrics
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    $windowReadyMs = $instance.ReadyMs
    # Split sentinels so echoed input cannot pass a parsed-viewport assertion.
    Send-NativeText $window ('@set VEX_SMOKE=VEX_NATIVE_SMOKE_' + "`r" + '@echo %VEX_SMOKE%OK' + "`r")
    Assert-NativeProbe $window 0 1 'real ConPTY round trip'
    if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 60, 60, 960, 600, 0x0040)) { throw 'Vex native smoke: window resize failed.' }
    Start-Sleep -Milliseconds 250
    Assert-NativeProbe $window 0 1 'resize preserves viewport'
    Send-NativeText $window ('@set VEX_FLOOD=VEX_NATIVE_FLOOD_' + "`r" + '@cls & for /l %i in (1,1,2000) do @echo NATIVE_FLOOD_%i' + "`r" + '@echo %VEX_FLOOD%OK' + "`r")
    Assert-NativeProbe $window 1 1 '2,000-line output settles'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-window.png')
    $process.Refresh()
    $singlePaneMemory = $process.WorkingSet64
    $cpuBefore = $process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 2
    $process.Refresh()
    $idleCpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuBefore

    Send-NativeText $window ('@cls' + "`r" + '@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%A' + "`r")
    Assert-NativeProbe $window 2 1 'first pane marker'
    Invoke-NativeAction $window 1
    Assert-NativeProbe $window 11 2 'split right creates two leaves'
    Assert-NativeProbe $window 13 2 'split owns two independent sessions'
    Assert-NativeProbe $window 2 0 'new pane does not inherit first pane output'
    Send-NativeText $window ('@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%B' + "`r")
    Assert-NativeProbe $window 3 1 'right pane has its own shell'
    [VexNativeSmoke]::SendWindowMessage($window, 0x201, [IntPtr]1, [IntPtr]((150 -shl 16) -bor 100)) | Out-Null
    Assert-NativeProbe $window 2 1 'click focuses first pane'
    Assert-NativeProbe $window 3 0 'first pane does not contain right pane output'
    Invoke-NativeAction $window 2
    Assert-NativeProbe $window 11 3 'nested split down creates a third leaf'
    $closedToken = Get-NativeProbe $window 12
    Send-NativeText $window ('@set VEX_PANE=VEX_NATIVE_PANE_' + "`r" + '@echo %VEX_PANE%C' + "`r")
    Assert-NativeProbe $window 6 1 'nested pane shell'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-workspace.png')
    $process.Refresh()
    $threePaneMemory = $process.WorkingSet64
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 11 2 'close promotes sibling'
    Assert-NativeProbe $window 2 1 'close focuses the first remaining leaf'
    [VexNativeSmoke]::SendWindowMessage($window, 0x8001, [IntPtr]$closedToken, [IntPtr]::Zero) | Out-Null
    Assert-NativeProbe $window 2 1 'stale output token cannot target a surviving pane'
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 11 1 'close collapses root split'
    Assert-NativeProbe $window 3 1 'sibling terminal state survives tree collapse'
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 10 1 'final tab stays open'
    Assert-NativeProbe $window 13 1 'closed pane workers/resources are removed'

    $paneDirectory = Join-Path $OutputDirectory ([string][char]0x017B + [char]0x00F3 + [char]0x0142 + [char]0x0107 + ' pane with spaces')
    New-Item -ItemType Directory -Force $paneDirectory | Out-Null
    Send-NativeText $window ('@cd /d "' + $paneDirectory + '"' + "`r")
    $reportDirectory = '@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command "[Console]::Write([char]27 + ' + "']7;' + ([uri](Get-Location).Path).AbsoluteUri + [char]7);[Console]::WriteLine('VEX_NATIVE_DIRECTORY_'+'REPORTED')" + '"'
    Send-NativeText $window ($reportDirectory + "`r")
    Assert-NativeProbe $window 30 1 'shell OSC 7 report crosses the real ConPTY stream'
    Invoke-NativeAction $window 1
    Send-NativeText $window ('@set VEX_DIRECTORY=VEX_NATIVE_DIRECTORY_' + "`r" + '@if /i "%CD%"=="' + $paneDirectory + '" echo %VEX_DIRECTORY%OK' + "`r")
    Assert-NativeProbe $window 8 1 'split inherits a reported Unicode/spaced working directory'
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 13 1 'directory-owning pane resources are freed on close'

    Send-NativeStatus $window 'state=working:app=build:progress=42'
    Assert-NativeProbe $window 31 2 'OSC 7501 crosses the real ConPTY stream'
    Invoke-NativeAction $window 1
    Assert-NativeProbe $window 31 0 'new pane does not inherit program status'
    Send-NativeStatus $window 'state=error:msg=RmFpbGVk'
    Assert-NativeProbe $window 31 5 'independent pane error status'
    Assert-NativeProbe $window 32 5 'tab aggregates the highest-priority pane status'
    Send-NativeText $window ('@rem acknowledge result' + "`r")
    Assert-NativeProbe $window 31 0 'user input acknowledges completed error results'
    Assert-NativeProbe $window 32 2 'acknowledgement exposes the sibling working status'
    Send-NativeStatus $window 'state=blocked:kind=permission'
    Assert-NativeProbe $window 31 4 'blocked permission state'
    Send-NativeText $window ('@rem blocked is not a completed result' + "`r")
    Assert-NativeProbe $window 31 4 'user input cannot dismiss a blocked record'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'native-status.png')
    Invoke-NativeAction $window 3
    Send-NativeStatus $window 'state=clear'
    Assert-NativeProbe $window 31 0 'closing a status-owning pane leaves its sibling independent'
    Invoke-NativeAction $window 0
    Send-NativeStatus $window 'state=error' 2000
    Invoke-NativeAction $window 5
    Start-Sleep -Milliseconds 300
    $beforeStatusPaints = Get-NativeProbe $window 20
    Assert-NativeProbe $window 33 5 'background program status updates while its tab is hidden'
    Assert-NativeProbe $window 31 0 'background status cannot overwrite the focused pane'
    Start-Sleep -Milliseconds 100
    if ((Get-NativeProbe $window 20) -le $beforeStatusPaints) { throw 'Vex native smoke: hidden status did not repaint the tab indicator.' }
    Invoke-NativeAction $window 4
    Assert-NativeProbe $window 31 5 'selecting a tab does not acknowledge its error result'
    Invoke-NativeAction $window 3

    Send-NativeText $window ('@set VEX_TAB=VEX_NATIVE_TAB_' + "`r" + '@echo %VEX_TAB%A' + "`r")
    Assert-NativeProbe $window 4 1 'first tab marker'
    Invoke-NativeAction $window 0
    Assert-NativeProbe $window 10 2 'new tab'
    $freshToken = Get-NativeProbe $window 12
    if ($freshToken -le $closedToken) { throw 'Vex native smoke: a pane token was reused.' }
    Assert-NativeProbe $window 4 0 'new tab output isolation'
    Send-NativeText $window ('@set VEX_ROOT=VEX_NATIVE_ROOT_DIRECTORY_' + "`r" + '@if /i "%CD%"=="' + $NativeWorkingDirectory + '" echo %VEX_ROOT%OK' + "`r")
    Assert-NativeProbe $window 9 1 'new tabs use the launch directory, not a changed pane directory'
    Send-NativeText $window ('@set VEX_TAB=VEX_NATIVE_TAB_' + "`r" + '@echo %VEX_TAB%B' + "`r")
    Assert-NativeProbe $window 5 1 'second tab marker'
    Invoke-NativeAction $window 5
    Assert-NativeProbe $window 4 1 'previous tab retains its terminal'
    Assert-NativeProbe $window 5 0 'previous tab cannot see second tab output'
    Invoke-NativeAction $window 4
    Assert-NativeProbe $window 5 1 'next tab retains its terminal'
    # Start plain output only after real process metadata settles, without racing a timed child sleep.
    $backgroundGateName = 'Local\VexBackgroundOutput-' + [Guid]::NewGuid().ToString('N')
    $backgroundGate = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $backgroundGateName)
    $backgroundFixture = "`$gate=[Threading.EventWaitHandle]::OpenExisting('$backgroundGateName');[Console]::Title='Future delayed command';[Console]::WriteLine('VEX_NATIVE_PANE_'+'C');`$gate.WaitOne()|Out-Null;`$gate.Dispose();[Console]::WriteLine('VEX_NATIVE_BACKGROUND_'+'OK');Start-Sleep -Milliseconds 3500;[Console]::Title='Finished hidden task';[Console]::WriteLine('VEX_NATIVE_FLOOD_'+'OK')"
    $backgroundEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($backgroundFixture))
    Send-NativeText $window ('@"' + $env:WINDIR + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -EncodedCommand ' + $backgroundEncoded + "`r")
    Assert-NativeProbe $window 6 1 'background writer is ready before the quiet-output sample'
    Assert-NativeProbe $window 94 10 'nested powershell process metadata is visible before the quiet-output sample'
    Invoke-NativeAction $window 5
    Start-Sleep -Milliseconds 300
    $hiddenTitleBefore = Get-NativeProbe $window 108
    $activeTitleBefore = Get-NativeProbe $window 109
    $paintCountBefore = Get-NativeProbe $window 20
    $backgroundGate.Set() | Out-Null
    Start-Sleep -Milliseconds 1200
    $backgroundPaints = (Get-NativeProbe $window 20) - $paintCountBefore
    if ($backgroundPaints -ne 0) {
        $hiddenTitleDelta = (Get-NativeProbe $window 108) - $hiddenTitleBefore
        $activeTitleDelta = (Get-NativeProbe $window 109) - $activeTitleBefore
        throw "Vex native smoke: background output caused $backgroundPaints active-window paints (hidden title delta=$hiddenTitleDelta, active title delta=$activeTitleDelta)."
    }
    Assert-NativeProbe $window 7 0 'hidden output does not change the active tab'
    $beforeTitlePaints = Get-NativeProbe $window 20
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ((Get-NativeProbe $window 108) -gt $hiddenTitleBefore -and (Get-NativeProbe $window 20) -gt $beforeTitlePaints) { break }
        Start-Sleep -Milliseconds 30
    } until ([DateTime]::UtcNow -gt $deadline)
    if ((Get-NativeProbe $window 108) -le $hiddenTitleBefore -or (Get-NativeProbe $window 20) -le $beforeTitlePaints) { throw 'Vex native smoke: a changed hidden title did not repaint its visible header.' }
    Assert-NativeProbe $window 47 0 'hidden title changes do not steal tab focus'
    Invoke-NativeAction $window 4
    Assert-NativeProbe $window 7 1 'hidden output is parsed before tab becomes visible'
    Assert-NativeProbe $window 1 1 'background writer completed before the next shell command'

    $terminalProcessId = Get-NativeProbe $window 14
    Send-NativeText $window ('@ping -n 100 127.0.0.1 >nul' + "`r")
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$terminalProcessId" | Where-Object { $_.Name -eq 'PING.EXE' })
    } until ($children.Count -gt 0 -or [DateTime]::UtcNow -gt $deadline)
    if ($children.Count -eq 0) { throw 'Vex native smoke: process-tree teardown fixture did not start its child.' }
    Invoke-NativeAction $window 7 1
    Assert-NativeProbe $window 10 1 'close active tab selects its left neighbor'
    Assert-NativeProbe $window 4 1 'neighbor terminal survives closing a busy tab'
    foreach ($child in $children) {
        if (Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue) { throw 'Vex native smoke: closed pane left a child process running.' }
    }
    if (Get-Process -Id $terminalProcessId -ErrorAction SilentlyContinue) { throw 'Vex native smoke: closed pane left its shell running.' }
    for ($index = 1; $index -lt 8; $index++) { Invoke-NativeAction $window 0 }
    Invoke-NativeAction $window 0
    Assert-NativeProbe $window 10 8 'terminal window enforces the eight-tab limit'
    Invoke-NativeAction $window 6 999
    Assert-NativeProbe $window 10 8 'invalid tab index is ignored safely'
    for ($index = 7; $index -gt 0; $index--) { Invoke-NativeAction $window 7 $index }
    Assert-NativeProbe $window 13 1 'closing background tabs releases their sessions'
    Close-NativeInstance $process $window
    $entryMilestones = @(Get-Content $metrics | ForEach-Object { [string]$_ })

    $env:VEX_NATIVE_METRICS = $null
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('@for /l %i in (1,1,1000000) do @echo NATIVE_BUSY_%i' + "`r")
    Start-Sleep -Milliseconds 200
    if (![VexNativeSmoke]::SetWindowPos($window, [IntPtr]::Zero, 80, 80, 1000, 650, 0x0040)) { throw 'Vex native smoke: flooding resize failed.' }
    Invoke-NativeAction $window 0
    Invoke-NativeAction $window 5
    Invoke-NativeAction $window 7 0
    Assert-NativeProbe $window 13 1 'closing a flooding background tab does not affect the survivor'
    Send-NativeText $window ('@for /l %i in (1,1,1000000) do @echo NATIVE_BUSY_%i' + "`r")
    Start-Sleep -Milliseconds 200
    Close-NativeInstance $process $window
    [pscustomobject]@{
        Result = 'PASS'
        Checks = @('ConPTY round trip/resize', '2,000-line output', 'nested right/down splits', 'click focus', 'isolated pane/tab output', 'stale output token rejection', 'background parsing', 'Unicode OSC 7 split directory', 'new-tab launch directory', 'OSC 7501 transport/priority/acknowledgement', 'hidden-tab status indicator repaint', 'close/focus fallback', '8-tab limit', 'process-tree teardown', 'resize under active output', 'flooding-tab close', 'active-output shutdown')
        WindowReadyMs = [Math]::Round($windowReadyMs, 2)
        SinglePaneWorkingSetBytes = $singlePaneMemory
        ThreePaneWorkingSetBytes = $threePaneMemory
        IdleCpuMsOverTwoSeconds = $idleCpuMs
        PaintsForHiddenTabOutput = $backgroundPaints
        EntryMilestones = $entryMilestones
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill() }
    if ($backgroundGate) { $backgroundGate.Dispose() }
    if ($previousDpiContext -and $previousDpiContext -ne [IntPtr]::Zero) { [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpiContext) | Out-Null }
    $env:VEX_NATIVE_METRICS = $oldMetrics
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
}
