$global:__vexOriginalPrompt = $function:prompt
$global:__vexCommandRunning = $false
function global:prompt {
    $ok = $?
    $code = if ($ok) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
    $esc = [char]27
    if ($global:__vexCommandRunning) {
        [Console]::Write("$esc]133;D;$code" + [char]7)
        $global:__vexCommandRunning = $false
    }
    [Console]::Write("$esc]133;A" + [char]7)
    if ($PWD.Provider.Name -eq 'FileSystem') {
        $uri = ([uri]$PWD.ProviderPath).AbsoluteUri
        [Console]::Write("$esc]7;$uri" + [char]7)
    }
    if ($global:__vexOriginalPrompt) { & $global:__vexOriginalPrompt } else { "PS $PWD> " }
}
try {
    Import-Module PSReadLine -ErrorAction Stop
    $global:__vexHistoryHandler = (Get-PSReadLineOption).AddToHistoryHandler
    Set-PSReadLineOption -AddToHistoryHandler {
        param($line)
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            $global:__vexCommandRunning = $true
            [Console]::Write([char]27 + ']133;C' + [char]7)
        }
        if ($global:__vexHistoryHandler) { $global:__vexHistoryHandler.Invoke($line) } else { $true }
    }
} catch {
    [Console]::WriteLine('Vex shell integration: PSReadLine unavailable; command-start tracking disabled.')
}
