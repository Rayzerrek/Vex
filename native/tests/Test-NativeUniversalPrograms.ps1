param(
    [string]$Executable = "$PSScriptRoot\..\zig-out\bin\vex-native.exe",
    [string]$OutputDirectory = "$PSScriptRoot\..\zig-out\verification\universal-programs"
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\DesktopHarness.ps1"
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$fixture = Join-Path $OutputDirectory ('future-' + [Guid]::NewGuid().ToString('N').Substring(0, 10) + '-' + (([string][char]0x97F3) * 20) + '.exe')
$helper = Join-Path $OutputDirectory ('helper-' + [IO.Path]::GetFileNameWithoutExtension($fixture).Substring(7, 10) + '.exe')
$documentDirectory = Join-Path $OutputDirectory 'folder with spaces\deep'
New-Item -ItemType Directory -Force $documentDirectory | Out-Null
$document = Join-Path $documentDirectory 'target-document.txt'
Set-Content $document 'path insertion must not execute a command'
$source = @'
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
public static class FutureTool {
    public static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--worker") { Thread.Sleep(60000); return; }
        var own = Assembly.GetExecutingAssembly().Location;
        var helper = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(own), "helper-" + System.IO.Path.GetFileNameWithoutExtension(own).Substring(7, 10) + ".exe");
        Process.Start(new ProcessStartInfo { FileName = helper, Arguments = "--worker", UseShellExecute = false });
        Console.WriteLine("VEX_NATIVE_SMOKE_OK");
        string line;
        while ((line = Console.ReadLine()) != null) {
            if (line == "clear") Console.Write("\u001b]7501;state=clear\u0007");
            if (line == Environment.GetEnvironmentVariable("VEX_TEST_PATH")) Console.WriteLine("VEX_NATIVE_FLOOD_OK");
            if (line == "report") Console.Write("\u001b]7501;state=blocked:kind=question:app=zeta-runtime:msg=V2FpdGluZyBmb3IgeW91\u0007");
        }
    }
}
'@
Add-Type -TypeDefinition $source -OutputAssembly $fixture -OutputType ConsoleApplication
Copy-Item $fixture $helper
$NativeWorkingDirectory = $OutputDirectory
$NativeLaunchArguments = '--program "' + $fixture + '"'
$previousPath = $env:VEX_TEST_PATH
$env:VEX_TEST_PATH = $document
$previousSelftest = $env:VEX_NATIVE_SELFTEST
$previousCapture = $env:VEX_NATIVE_CAPTURE_BMP
$previousDpi = [VexNativeSmoke]::SetThreadDpiAwarenessContext([IntPtr](-4))
$env:VEX_NATIVE_CAPTURE_BMP = Join-Path $OutputDirectory '.capture.bmp'
$process = $null
try {
    $env:VEX_NATIVE_SELFTEST = '1'
    $instance = Start-NativeInstance
    $process = $instance.Process
    $window = $instance.Window
    Assert-NativeProbe $window 0 1 'unknown custom executable owns a real ConPTY session'
    $stem = [IO.Path]::GetFileNameWithoutExtension($fixture)
    $displayBytes = [Text.Encoding]::UTF8.GetBytes($stem)
    $displayLength = [Math]::Min(64, $displayBytes.Length)
    while ($displayLength -lt $displayBytes.Length -and ($displayBytes[$displayLength] -band 0xC0) -eq 0x80) { $displayLength-- }
    Assert-NativeProbe $window 94 $displayLength 'Unicode root is detected without splitting UTF-8 and its helper cannot steal identity'
    Assert-NativeProbe $window 92 2 'unknown program gets a deterministic badge, not another brand'
    $pidBefore = Get-NativeProbe $window 14
    Invoke-NativeAction $window 12
    Assert-NativeProbe $window 104 0 'background index completes'
    Send-NativeText $window 'target-document'
    Assert-NativeProbe $window 55 1 'fuzzy path search includes nested folders'
    Save-NativeScreenshot $window (Join-Path $OutputDirectory 'recursive-path-popup.png')
    Invoke-NativeAction $window 12 98
    Send-NativeText $window "`r"
    Assert-NativeProbe $window 1 1 'path is pasted literally into an arbitrary program, with no shell quotes or execution'
    Send-NativeText $window "report`r"
    Assert-NativeProbe $window 31 4 'unknown program emits standard blocked status without registration'
    Assert-NativeProbe $window 93 ([int][char]'Z') 'explicit identity works for an unknown runtime'
    Send-NativeText $window "clear`r"
    Assert-NativeProbe $window 31 0 'explicit clear removes the report'
    Assert-NativeProbe $window 93 ([int][char]'F') 'clearing metadata restores the cached process icon'
    Assert-NativeProbe $window 14 $pidBefore 'indexing and status do not recreate the session'
    Invoke-NativeAction $window 0
    Invoke-NativeAction $window 6 0
    Invoke-NativeAction $window 16 0
    Assert-NativeProbe $window 53 5 'unknown program receives a running-work close warning'
    [VexNativeSmoke]::SendWindowMessage($window, 0x100, [IntPtr]0x1B, [IntPtr]::Zero) | Out-Null
    Close-NativeInstance $process $window
    $process = $null
    [pscustomobject]@{ Result = 'PASS'; Checks = @('random executable name', 'root process and worker ownership', 'unknown icon fallback', 'standard status protocol', 'recursive background path search', 'literal insertion into arbitrary stdin', 'close protection', 'stable PTY') } | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'result.json')
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:VEX_NATIVE_SELFTEST = $previousSelftest
    $env:VEX_TEST_PATH = $previousPath
    $env:VEX_NATIVE_CAPTURE_BMP = $previousCapture
    [VexNativeSmoke]::SetThreadDpiAwarenessContext($previousDpi) | Out-Null
    Remove-Item $fixture, $helper -ErrorAction SilentlyContinue
}
