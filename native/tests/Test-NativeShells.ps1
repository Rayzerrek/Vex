param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
$NativeWorkingDirectory = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$oldSelftest = $env:VEX_NATIVE_SELFTEST
$oldTerm = $env:TERM
$oldColorTerm = $env:COLORTERM
$process = $null
$results = @()
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $env:TERM = 'dumb'
    $env:COLORTERM = $null
    foreach ($profile in @('cmd', 'powershell', 'pwsh', 'nu', 'gitbash')) {
        $instance = Start-NativeInstance -Arguments ('--shell ' + $profile)
        $process = $instance.Process
        $window = $instance.Window
        $expectedKind = @{ cmd = 1; powershell = 2; pwsh = 3; nu = 4; gitbash = 5 }[$profile]
        $actualKind = Get-NativeProbe $window 34
        if ($actualKind -eq 1 -and $expectedKind -ne 1) {
            Close-NativeInstance $process $window
            $results += [pscustomobject]@{ Shell = $profile; Result = 'SKIP'; Reason = 'not installed; resolved to system cmd' }
            continue
        }
        Assert-NativeProbe $window 34 $expectedKind 'requested profile actually launched'
        switch ($profile) {
            'cmd' { Send-NativeText $window ('@set VEX_READY=VEX_NATIVE_SHELL_' + "`r" + '@echo %VEX_READY%READY' + "`r") }
            { $_ -in 'powershell', 'pwsh' } { Send-NativeText $window ("Set-PSReadLineOption -HistorySaveStyle SaveNothing; [Console]::WriteLine('VEX_NATIVE_SHELL_'+'READY')" + "`r") }
            'nu' { Send-NativeText $window ('print ("VEX_NATIVE_SHELL_" + "READY")' + "`r") }
            'gitbash' { Send-NativeText $window ("printf '%s%s\n' VEX_NATIVE_SHELL_ READY" + "`r") }
        }
        Assert-NativeProbe $window 35 1 ($profile + ' shell round trip')
        if ($profile -ne 'gitbash') { Assert-NativeProbe $window 107 1 ($profile + ' integrated prompt becomes idle') }
        Assert-NativeProbe $window 31 0 ($profile + ' prompt markers do not fabricate program status')
        switch ($profile) {
            'cmd' { Send-NativeText $window ('@if "%TERM%/%COLORTERM%"=="xterm-256color/truecolor" echo %VEX_READY%ENV_OK' + "`r") }
            { $_ -in 'powershell', 'pwsh' } { Send-NativeText $window ("if (`$env:TERM -eq 'xterm-256color' -and `$env:COLORTERM -eq 'truecolor') { [Console]::WriteLine('VEX_NATIVE_SHELL_'+'ENV_OK') }" + "`r") }
            'nu' { Send-NativeText $window ('if $env.TERM == "xterm-256color" and $env.COLORTERM == "truecolor" { print ("VEX_NATIVE_SHELL_" + "ENV_OK") }' + "`r") }
            'gitbash' { Send-NativeText $window ('if [ "$TERM/$COLORTERM" = xterm-256color/truecolor ]; then printf "%s%s\n" VEX_NATIVE_SHELL_ ENV_OK; fi' + "`r") }
        }
        Assert-NativeProbe $window 37 1 ($profile + ' child environment defaults')
        if ($profile -in 'powershell', 'pwsh', 'nu') {
            $folder = Join-Path $OutputDirectory ([string][char]0x017B + [char]0x00F3 + [char]0x0142 + [char]0x0107 + ' shell folder')
            New-Item -ItemType Directory -Force $folder | Out-Null
            if ($profile -eq 'nu') { $changeDirectory = 'cd "' + $folder.Replace('\', '\\').Replace('"', '\"') + '"' }
            else { $changeDirectory = "Set-Location -LiteralPath '" + $folder.Replace("'", "''") + "'" }
            Send-NativeText $window ($changeDirectory + "`r")
            Assert-NativeProbe $window 107 1 ($profile + ' directory command completes')
            # Wait for the prompt's actual OSC 7 callback, not merely a previous idle frame.
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $reported = Get-NativeProbe $window 39
                if ($reported -eq 1) { break }
                Start-Sleep -Milliseconds 30
            } until ([DateTime]::UtcNow -gt $deadline)
            if ($reported -ne 1) { throw "Vex native smoke: $profile did not report a live Unicode directory." }
            Invoke-NativeAction $window 1
            Assert-NativeProbe $window 107 1 ($profile + ' split owns an independent integrated shell')
            if ($profile -eq 'nu') { $probe = 'if $env.PWD == "' + $folder.Replace('\', '\\') + '" { print ("VEX_NATIVE_SHELL_" + "DIRECTORY_OK") }' }
            else { $probe = "if (`$PWD.ProviderPath -eq '" + $folder.Replace("'", "''") + "') { [Console]::WriteLine('VEX_NATIVE_SHELL_'+'DIRECTORY_OK') }" }
            Send-NativeText $window ($probe + "`r")
            Assert-NativeProbe $window 36 1 ($profile + ' split inherits prompt-reported Unicode directory')
            if ($profile -eq 'nu') { Send-NativeText $window ('sleep 2500ms' + "`r") }
            else { Send-NativeText $window ('Start-Sleep -Milliseconds 2500' + "`r") }
            Assert-NativeProbe $window 107 2 ($profile + ' command-start marker')
            Assert-NativeProbe $window 31 0 ($profile + ' running shell command does not fabricate program status')
            Assert-NativeProbe $window 107 3 ($profile + ' long command completed lifecycle')
            if ($profile -eq 'nu') { Send-NativeText $window ('run-external $env.ComSpec /d /c exit 7' + "`r") }
            else { Send-NativeText $window ('& $env:ComSpec /d /c exit 7' + "`r") }
            Assert-NativeProbe $window 107 5 ($profile + ' failed shell command lifecycle')
            Assert-NativeProbe $window 31 0 ($profile + ' failed shell command does not fabricate program status')
        }
        Close-NativeInstance $process $window
        $results += [pscustomobject]@{ Shell = $profile; Result = 'PASS' }
    }
    $copyDirectory = Join-Path $OutputDirectory ([string][char]0x017B + ' native install with spaces')
    New-Item -ItemType Directory -Force $copyDirectory | Out-Null
    Copy-Item $Executable (Join-Path $copyDirectory 'vex-native.exe') -Force
    Copy-Item (Join-Path (Split-Path $Executable) 'ghostty-vt.dll') $copyDirectory -Force
    Copy-Item (Join-Path (Split-Path $Executable) 'ShellIntegration') $copyDirectory -Recurse -Force
    $instance = Start-NativeInstance -Arguments '--shell cmd' -ExecutablePath (Join-Path $copyDirectory 'vex-native.exe')
    $process = $instance.Process
    $window = $instance.Window
    Send-NativeText $window ('@set VEX_READY=VEX_NATIVE_SHELL_' + "`r" + '@echo %VEX_READY%READY' + "`r")
    Assert-NativeProbe $window 35 1 'cmd bootstrap resolves a Unicode/spaced installation path'
    Assert-NativeProbe $window 107 1 'cmd bootstrap actually emits its prompt marker'
    Assert-NativeProbe $window 31 0 'cmd bootstrap markers remain distinct from program status'
    Close-NativeInstance $process $window
    $results += [pscustomobject]@{ Shell = 'cmd Unicode install'; Result = 'PASS' }
    $results | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'shells.json')
    $results | ConvertTo-Json
} finally {
    if ($null -ne $process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $env:VEX_NATIVE_SELFTEST = $oldSelftest
    $env:TERM = $oldTerm
    $env:COLORTERM = $oldColorTerm
}
