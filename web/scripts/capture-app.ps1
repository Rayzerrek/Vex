param(
    [Parameter(Mandatory)] [string] $AppPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VexWindowCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@

$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$output = Join-Path $repo 'web/public/shots'
$profile = Join-Path ([IO.Path]::GetTempPath()) ('vex-site-shots-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $profile -Force | Out-Null
$previousProfile = $env:VEX_PROFILE_DIR
$process = $null

function Save-VexWindow([string] $Name) {
    $rect = [VexWindowCapture+Rect]::new()
    if (-not [VexWindowCapture]::GetWindowRect($process.MainWindowHandle, [ref] $rect)) {
        throw 'Vex screenshot: window bounds unavailable.'
    }
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try {
        if (-not [VexWindowCapture]::PrintWindow($process.MainWindowHandle, $dc, 2)) {
            throw 'Vex screenshot: PrintWindow failed.'
        }
    } finally {
        $graphics.ReleaseHdc($dc)
        $graphics.Dispose()
    }
    try { $bitmap.Save((Join-Path $output $Name), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

try {
    # A disposable profile and scripted shell output keep screenshots reproducible,
    # without opening programs or modifying the user's projects and sessions.
    $fixtures = @{
        code = @'
$e = [char]27
[Console]::Write("${e}]0;PowerShell${e}\${e}[2J${e}[H")
[Console]::Write(@"
${e}[90m~/code/vex ${e}[34mmain${e}[0m
${e}[34m> ${e}[0mgit diff --stat

 windows/Vex.App/MainWindow.xaml       | 18 +++++++---
 windows/Vex.App/Model/Workspace.cs    | 12 ++++++--
 web/src/app.tsx                      | 24 ++++++++++

${e}[32m 3 files changed, 42 insertions(+), 12 deletions(-)${e}[0m

${e}[90m~/code/vex ${e}[34mmain${e}[0m
${e}[34m> ${e}[0mdotnet build windows/Vex.slnx

  Vex.Terminal    ${e}[90m-> net10.0${e}[0m
  Vex.Libghostty  ${e}[90m-> net10.0${e}[0m
  Vex.App        ${e}[90m-> net10.0-windows${e}[0m

${e}[32mBuild succeeded.${e}[0m
    0 Warning(s)
    0 Error(s)

${e}[90m~/code/vex ${e}[34mmain${e}[0m
"@)
function global:prompt { [Console]::Write("`e[34m> `e[0m"); ' ' }
'@
        server = @'
$e = [char]27
[Console]::Write("${e}]0;Dev server${e}\${e}[2J${e}[H")
[Console]::Write(@"
${e}[90m~/code/vex/web${e}[0m
${e}[34m> ${e}[0mpnpm dev

  ${e}[32mVITE+${e}[0m  ${e}[90mready in 182 ms${e}[0m

  ${e}[32m> ${e}[0mLocal:   ${e}[34mhttp://localhost:5173/${e}[0m
  ${e}[90m> ${e}[0mNetwork: use --host to expose

  ${e}[90mpress h + enter to show help${e}[0m

${e}[90m14:32:08${e}[0m  ${e}[32mhmr update${e}[0m /src/app.tsx
${e}[90m14:32:12${e}[0m  ${e}[32mhmr update${e}[0m /src/index.css
"@)
function global:prompt { ' ' }
'@
        tests = @'
$e = [char]27
[Console]::Write("${e}]0;Checks${e}\${e}[2J${e}[H")
[Console]::Write(@"
${e}[90m~/code/vex/web${e}[0m
${e}[34m> ${e}[0mpnpm test -- --run

 ${e}[32mPASS${e}[0m  src/site.test.tsx

 ${e}[32m✓${e}[0m documentation anchors
 ${e}[32m✓${e}[0m direct links and page titles
 ${e}[32m✓${e}[0m static rendering

 ${e}[32mTests  5 passed${e}[0m
 ${e}[90mReady for the next change.${e}[0m
"@)
function global:prompt { [Console]::Write("`e[34m> `e[0m"); ' ' }
'@
    }
    $shells = foreach ($name in @('code', 'server', 'tests')) {
        $file = Join-Path $profile "$name.ps1"
        Set-Content $file ('[Console]::OutputEncoding = [Text.UTF8Encoding]::new()' + "`n" + $fixtures[$name]) -Encoding utf8
        @{
            Id = "site-$name"; Name = switch ($name) { code { 'PowerShell' } server { 'Dev server' } tests { 'Checks' } }
            Program = (Get-Command pwsh).Source
            Arguments = "-NoLogo -NoProfile -NoExit -File `"$file`""
        }
    }
    $terminal = { param($shell, $focused) [ordered]@{ type = 'terminal'; ShellId = "site-$shell"; WorkingDirectory = $repo; IsFocused = $focused } }
    $session = @{
        Windows = @(@{
            SelectedProjectIndex = 0
            Projects = @(@{
                Name = 'Vex'; WorkingDirectory = $repo; DefaultShellId = 'site-code'; SelectedTabIndex = 0
                Tabs = @(
                    @{ Title = 'Development'; HasCustomTitle = $true; Root = [ordered]@{
                        type = 'split'; Orientation = 'Horizontal'; Ratio = 0.58
                        First = (& $terminal code $true)
                        Second = [ordered]@{ type = 'split'; Orientation = 'Vertical'; Ratio = 0.52; First = (& $terminal server $false); Second = (& $terminal tests $false) }
                    } },
                    @{ Title = 'Shell'; HasCustomTitle = $true; Root = (& $terminal code $true) }
                )
            })
        })
    }
    $env:VEX_PROFILE_DIR = $profile
    foreach ($appearance in @('Dark', 'Light')) {
        @{ Appearance = $appearance; ThemeName = "Vex $appearance"; FontSize = 14; CursorBlink = $false; ShellId = 'site-code'; CustomShells = @($shells); ConfirmOnExit = $false } |
            ConvertTo-Json -Depth 12 | Set-Content (Join-Path $profile 'settings.json') -Encoding utf8
        $session | ConvertTo-Json -Depth 16 | Set-Content (Join-Path $profile 'session.json') -Encoding utf8
        $process = Start-Process (Resolve-Path $AppPath).Path -PassThru
        for ($attempt = 0; $attempt -lt 50; $attempt++) {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited) { throw 'Vex screenshot: application exited before capture.' }
            if ($process.MainWindowHandle -ne 0) { break }
        }
        [VexWindowCapture]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 40, 40, 1440, 900, 0x0040) | Out-Null
        [VexWindowCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        Start-Sleep -Seconds 4
        Save-VexWindow "vex-$($appearance.ToLower()).png"
        if ($appearance -eq 'Dark') {
            [Windows.Forms.SendKeys]::SendWait('^+p')
            Start-Sleep -Milliseconds 800
            Save-VexWindow 'vex-palette.png'
            [Windows.Forms.SendKeys]::SendWait('{ESC}')
            Start-Sleep -Milliseconds 400
            foreach ($key in @(0x11, 0x10, 0x46)) {
                [VexWindowCapture]::keybd_event($key, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 50
            }
            foreach ($key in @(0x46, 0x10, 0x11)) {
                [VexWindowCapture]::keybd_event($key, 0, 2, [UIntPtr]::Zero)
            }
            Start-Sleep -Milliseconds 800
            Save-VexWindow 'vex-path-picker.png'
        }
        $process.Kill()
        $process.WaitForExit()
        $process = $null
    }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_PROFILE_DIR = $previousProfile
    Remove-Item $profile -Recurse -Force
}
Write-Host "Captured Vex screenshots in $output"
