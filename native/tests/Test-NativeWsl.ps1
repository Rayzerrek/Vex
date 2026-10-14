param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification",
    [string]$Distribution = 'Ubuntu'
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldTerm = $env:TERM
$oldColorTerm = $env:COLORTERM
$process = $null
$linuxFolder = '/tmp/vex-native-' + [Guid]::NewGuid().ToString('N') + ' ' + [char]0x017B + [char]0x00F3 + [char]0x0142 + [char]0x0107 + " O'Brien"
function Quote-PosixArgument([string]$Text) {
    $escapedQuote = "'" + [char]34 + "'" + [char]34 + "'"
    return "'" + $Text.Replace("'", $escapedQuote) + "'"
}
function Invoke-WslFixture([string]$Command) {
    # Windows PowerShell's native argv marshaller removes embedded quotes; keep fixture scripts out of argv.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Command))
    $output = & "$env:WINDIR\System32\wsl.exe" --distribution $Distribution --exec sh -c "printf %s $encoded | base64 -d | sh"
    if ($LASTEXITCODE -ne 0) { throw "Vex native WSL smoke: fixture command failed ($LASTEXITCODE)." }
    return $output
}
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:TERM = 'dumb'
    $env:COLORTERM = $null
    $defaultKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Lxss')
    if ($null -eq $defaultKey) { throw 'Vex native WSL smoke: no default distribution is registered.' }
    try {
        $defaultGuid = $defaultKey.GetValue('DefaultDistribution')
        if ($defaultGuid -isnot [string] -or $defaultGuid -notmatch '^\{[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}$') { throw 'Vex native WSL smoke: invalid default distribution GUID.' }
        $distributionKey = $defaultKey.OpenSubKey($defaultGuid)
        if ($null -eq $distributionKey) { throw 'Vex native WSL smoke: default distribution key is missing.' }
        try { $defaultDistribution = $distributionKey.GetValue('DistributionName') }
        finally { $distributionKey.Dispose() }
        if ($defaultDistribution -isnot [string] -or $defaultDistribution.Length -eq 0) { throw 'Vex native WSL smoke: invalid default distribution name.' }
    } finally { $defaultKey.Dispose() }
    $instance = Start-NativeInstance -Arguments '--shell wsl'
    $process = $instance.Process
    $window = $instance.Window
    Assert-NativeProbe $window 34 7 'automatic distribution uses WSL without a cmd fallback'
    $defaultCheck = '[ "$WSL_DISTRO_NAME" = ' + (Quote-PosixArgument $defaultDistribution) + ' ] && printf "%s%s\n" VEX_NATIVE_SHELL_ DIRECTORY_OK'
    Send-NativeText $window ($defaultCheck + "`r")
    Assert-NativeProbe $window 36 1 'automatic WSL launch selects the registered default distribution'
    Invoke-NativeAction $window 1
    Send-NativeText $window ($defaultCheck + "`r")
    Assert-NativeProbe $window 36 1 'split retains the resolved default distribution'
    Close-NativeInstance $process $window

    Invoke-WslFixture ('mkdir -p -- ' + (Quote-PosixArgument $linuxFolder)) | Out-Null
    $linuxProgram = $linuxFolder + '/custom bash program'
    Invoke-WslFixture ('ln -s -- /bin/bash ' + (Quote-PosixArgument $linuxProgram)) | Out-Null
    $quotedDistribution = '"' + $Distribution + '"'
    $instance = Start-NativeInstance -Arguments ('--shell wsl --distribution ' + $quotedDistribution)
    $process = $instance.Process
    $window = $instance.Window
    Assert-NativeProbe $window 34 7 'default Linux shell uses the WSL backend'
    Send-NativeText $window ("printf '%s%s\n' VEX_NATIVE_SHELL_ READY" + "`r")
    Assert-NativeProbe $window 35 1 'default Linux login shell round trip'
    Send-NativeText $window ('if [ "$TERM/$COLORTERM" = xterm-256color/truecolor ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ ENV_OK; fi' + "`r")
    Assert-NativeProbe $window 37 1 'Linux child terminal environment defaults'
    Close-NativeInstance $process $window

    $env:TERM = 'vt100'
    $env:COLORTERM = 'user-value'
    $arguments = '--shell "wsl:' + $linuxProgram + '" --distribution ' + $quotedDistribution + ' --arguments "--noprofile --norc"'
    $instance = Start-NativeInstance -Arguments $arguments
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ("printf '%s%s\n' VEX_NATIVE_SHELL_ READY" + "`r")
    Assert-NativeProbe $window 35 1 'explicit Unicode/spaced/apostrophe Linux program and arguments round trip'
    Send-NativeText $window ('if [ "$TERM/$COLORTERM" = vt100/user-value ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ ENV_OK; fi' + "`r")
    Assert-NativeProbe $window 37 1 'Linux child environment preserves explicit overrides'
    $quotedFolder = Quote-PosixArgument $linuxFolder
    $uri = 'file://localhost' + (($linuxFolder.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/')
    Send-NativeText $window ('cd -- ' + $quotedFolder + "; printf '\033]7;%s\007' " + (Quote-PosixArgument $uri) + "`r")
    Assert-NativeProbe $window 39 1 'Linux Unicode/spaced OSC 7 directory crosses real ConPTY'
    Invoke-NativeAction $window 1
    Assert-NativeProbe $window 11 2 'WSL split owns an independent terminal'
    Send-NativeText $window ('if [ "$PWD" = ' + $quotedFolder + ' ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ DIRECTORY_OK; fi' + "`r")
    Assert-NativeProbe $window 36 1 'WSL split inherits Linux Unicode/spaced/apostrophe directory'
    $pidFile = Quote-PosixArgument ($linuxFolder + '/pane.pid')
    Send-NativeText $window ('sleep 300 & printf "%s\n" "$$" "$!" > ' + $pidFile + "; printf '%s%s\n' VEX_NATIVE_DIRECTORY_ REPORTED; wait" + "`r")
    Assert-NativeProbe $window 30 1 'Linux pane PID fixture is written by the actual shell'
    $linuxPids = @(Invoke-WslFixture ('cat -- ' + $pidFile) | ForEach-Object { [int]$_ })
    if ($linuxPids.Count -ne 2) { throw 'Vex native WSL smoke: shell/child PID fixture is incomplete.' }
    Invoke-NativeAction $window 3
    Assert-NativeProbe $window 13 1 'closing a WSL split frees its independent native session'
    Start-Sleep -Milliseconds 500
    foreach ($linuxPid in $linuxPids) {
        $alive = Invoke-WslFixture ("if kill -0 $linuxPid 2>/dev/null; then printf alive; else printf stopped; fi")
        if ($alive -ne 'stopped') { throw "Vex native WSL smoke: closed pane left Linux process $linuxPid running." }
    }
    Invoke-NativeAction $window 0
    $linuxProject = [string](Invoke-WslFixture ('wslpath -u ' + (Quote-PosixArgument $NativeWorkingDirectory)))
    Send-NativeText $window ('if [ "$PWD" = ' + (Quote-PosixArgument $linuxProject.Trim()) + ' ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ DIRECTORY_OK; fi' + "`r")
    Assert-NativeProbe $window 36 1 'new WSL tabs use the project directory rather than a changed pane directory'
    Close-NativeInstance $process $window

    $networkFolder = '\\wsl.localhost\' + $Distribution + $linuxFolder.Replace('/', '\')
    $instance = Start-NativeInstance -Arguments $arguments -WorkingDirectory $networkFolder
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('if [ "$PWD" = ' + $quotedFolder + ' ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ DIRECTORY_OK; fi' + "`r")
    Assert-NativeProbe $window 36 1 'WSL network project starts in the selected Linux distribution directory'
    Close-NativeInstance $process $window

    $instance = Start-NativeInstance -Arguments '' -WorkingDirectory $networkFolder
    $process = $instance.Process
    $window = $instance.Window
    Assert-NativeProbe $window 34 7 'WSL project chooses Linux instead of system cmd without CLI shell flags'
    Send-NativeText $window ('if [ "$PWD" = ' + $quotedFolder + ' ] && [ "$WSL_DISTRO_NAME" = ' + (Quote-PosixArgument $Distribution) + ' ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ DIRECTORY_OK; fi' + "`r")
    Assert-NativeProbe $window 36 1 'UNC project pins its own distribution and Linux directory automatically'
    Close-NativeInstance $process $window
    [pscustomobject]@{
        Result = 'PASS'
        Distribution = $Distribution
        RegisteredDefaultDistribution = $defaultDistribution
        Checks = @('registered default selection without CLI distribution', 'pinned default in split', 'automatic WSL UNC project selection', 'default login shell', 'Unicode/spaced/apostrophe explicit Linux program/arguments', 'Linux TERM/COLORTERM defaults/overrides', 'Unicode Linux OSC 7', 'split directory inheritance', 'new-tab project directory', 'Linux shell/child shutdown on busy pane close', 'WSL UNC project startup')
    } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $OutputDirectory 'wsl.json')
} finally {
    if ($null -ne $process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    try { Invoke-WslFixture ('rm -rf -- ' + (Quote-PosixArgument $linuxFolder)) | Out-Null }
    finally {
        $env:VEX_NATIVE_SELFTEST = $oldSelftest
        $env:TERM = $oldTerm
        $env:COLORTERM = $oldColorTerm
    }
}
