[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$App = Split-Path $PSScriptRoot -Parent
$Fixture = Join-Path $App 'zig-out/bin/vex-conpty-fixture.exe'
if (!(Test-Path $Fixture)) { throw 'Build the native fixture first: zig build test-build' }
$Root = Join-Path $App ('publish/update-installer-fixture-' + [Guid]::NewGuid().ToString('N'))
$Install = Join-Path $env:LOCALAPPDATA ('Vex updater test żółć ' + [Guid]::NewGuid().ToString('N'))
$Upgrade = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'
$ProductionUpgrade = '{F3CD9022-815C-4211-AC09-2938D95B26A4}'
if (!$PSCmdlet.ShouldProcess($Install, 'Install, upgrade, roll back and uninstall isolated test MSI products (never the real Vex product)')) { return }
$null = New-Item -ItemType Directory -Force $Root
$Stage = Join-Path $Root 'stage'
$null = New-Item -ItemType Directory $Stage
$Installer = New-Object -ComObject WindowsInstaller.Installer
$Products = @()
$Held = $null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class VexInstallerFixture {
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr window, uint message, UIntPtr w, IntPtr l);
}
'@
function Run-Tool([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Installer fixture tool failed: $Program ($LASTEXITCODE)" }
}
function Get-ProductCode([string]$Path) {
    $db = $Installer.OpenDatabase($Path, 0)
    $view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductCode'")
    $null = $view.Execute()
    $record = $view.Fetch()
    $value = $record.StringData(1)
    $null = $view.Close()
    foreach ($object in $record,$view,$db) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($object) }
    if ($value -notmatch '^\{[0-9A-Fa-f-]{36}\}$') { throw 'Invalid test ProductCode' }
    $value
}
function Stage-Update([string]$Version) {
    $script:Stage = Join-Path $Root "stage-$Version"
    $null = New-Item -ItemType Directory -Force $Stage
    $msi = Join-Path $Root "$Version/bin/Release/Fixture.msi"
    Copy-Item $msi (Join-Path $Stage 'update.msi') -Force
    [IO.File]::WriteAllText((Join-Path $Stage 'manifest.txt'), "VEX-UPDATE-1`n$Version`n$((Get-Item $msi).Length)`n$((Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant())`n", [Text.Encoding]::ASCII)
    Remove-Item (Join-Path $Stage 'fixture-preflight.txt') -ErrorAction SilentlyContinue
}
function Invoke-Parent([string]$Mode, [int]$Expected) {
    $parent = Start-Process (Join-Path $Install 'Vex.exe') -ArgumentList @('--update-parent',('"' + $Stage + '"'),$Mode) -WorkingDirectory $Install -PassThru
    try {
        if (!$parent.WaitForExit(30000)) { throw "Installer preflight timed out (PID $($parent.Id))" }
        if ($parent.ExitCode -ne $Expected) { throw "Installer preflight expected $Expected, got $($parent.ExitCode)" }
    } finally {
        if (!$parent.HasExited) { $parent.Kill(); $parent.WaitForExit() }
        $parent.Dispose()
    }
}
function Wait-Restart([bool]$Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while (!(Test-Path (Join-Path $Install 'restarted-1.0.1.marker'))) {
        if ($Failure) {
            foreach ($helper in Get-Process vex-update -ErrorAction SilentlyContinue) {
                if ($helper.Path -eq (Join-Path $Stage 'vex-update.exe') -and $helper.MainWindowTitle -eq 'Vex update') {
                    $null = [VexInstallerFixture]::SendMessageW($helper.MainWindowHandle, 0x111, [UIntPtr]1, [IntPtr]::Zero)
                }
            }
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Updated/rolled-back fixture did not restart' }
        Start-Sleep -Milliseconds 100
    }
    if ([IO.File]::ReadAllText((Join-Path $Install 'fixture-version.txt')) -ne '1.0.1') { throw 'Unexpected installed version after transaction' }
    foreach ($name in 'Vex.exe','vex-update.exe') {
        if ((Get-FileHash (Join-Path $Install $name)).Hash -ne (Get-FileHash (Join-Path $Root "1.0.1/$name")).Hash) { throw "Upgrade/rollback did not preserve the expected binary: $name" }
    }
    # The marker is written just before process exit; wait for file mappings before the next MSI.
    Start-Sleep -Milliseconds 250
}
try {
    foreach ($file in 'update_manifest.h','update_package.h','update_package.cpp','update_install_guard.h','update_install_guard.cpp','update_installer.cpp') {
        Copy-Item (Join-Path $App "src/windows/update/$file") $Root
    }
    $header = Join-Path $Root 'update_package.h'
    $source = [IO.File]::ReadAllText($header)
    if ([regex]::Matches($source, [regex]::Escape($ProductionUpgrade)).Count -ne 1 -or $Upgrade -eq $ProductionUpgrade) { throw 'Could not isolate the fixture UpgradeCode' }
    [IO.File]::WriteAllText($header, $source.Replace($ProductionUpgrade, $Upgrade))
    $helper = Join-Path $Root 'vex-update.exe'
    Run-Tool zig @('c++','-target','x86_64-windows-gnu','-std=c++20','-fno-exceptions','-fno-rtti','-DVEX_VERSION="1.0.0"',(Join-Path $Root 'update_installer.cpp'),(Join-Path $Root 'update_package.cpp'),(Join-Path $Root 'update_install_guard.cpp'),'-lshell32','-luser32','-ladvapi32','-lbcrypt','-lmsi','-o',$helper)
    $failureDll = Join-Path $Root 'fixture-failure.dll'
    $failureSource = Join-Path $Root 'fixture-failure.c'
    [IO.File]::WriteAllText($failureSource, '#include <windows.h>
#include <msi.h>
__declspec(dllexport) UINT __stdcall FailUpdate(MSIHANDLE handle) { (void)handle; return ERROR_INSTALL_FAILURE; }
')
    Run-Tool zig @('cc','-target','x86_64-windows-gnu','-shared',$failureSource,'-o',$failureDll)
    $padding = New-Object byte[] (1024 * 1024)
    [Security.Cryptography.RandomNumberGenerator]::Fill($padding)
    [IO.File]::WriteAllBytes((Join-Path $Root 'padding.bin'), $padding)
    foreach ($version in '1.0.0','1.0.1','1.0.2') {
        $folder = Join-Path $Root $version
        $null = New-Item -ItemType Directory $folder
        [IO.File]::WriteAllText((Join-Path $folder 'fixture-version.txt'), $version)
        foreach ($item in @(@{source=$Fixture; name='Vex.exe'},@{source=$helper; name='vex-update.exe'})) {
            $path = Join-Path $folder $item.name
            Copy-Item $item.source $path
            $stream = [IO.File]::Open($path,[IO.FileMode]::Append)
            try { $stream.Write([Text.Encoding]::ASCII.GetBytes("fixture-$version")) } finally { $stream.Dispose() }
        }
        $fail = if ($version -eq '1.0.2') { '<Binary Id="FailureCode" SourceFile="' + $failureDll + '" /><CustomAction Id="DeliberateFailure" BinaryRef="FailureCode" DllEntry="FailUpdate" Execute="deferred" Return="check" Impersonate="yes" /><InstallExecuteSequence><Custom Action="DeliberateFailure" After="InstallFiles" Condition="NOT REMOVE" /></InstallExecuteSequence>' } else { '' }
        $wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"><Package Name="Vex Updater Isolated Test $Upgrade" Manufacturer="Vex test" Version="$version.0" UpgradeCode="$Upgrade" Scope="perUserOrMachine">
<MajorUpgrade Schedule="afterInstallExecute" DowngradeErrorMessage="Downgrade blocked" /><MediaTemplate EmbedCab="yes" />
<Property Id="INSTALLFOLDER" Secure="yes" /><SetProperty Id="ARPINSTALLLOCATION" Value="[INSTALLFOLDER]" After="CostFinalize" Sequence="execute" />
<Feature Id="Main"><ComponentGroupRef Id="Payload" /></Feature>
<StandardDirectory Id="ProgramFiles64Folder"><Directory Id="INSTALLFOLDER" Name="VexUpdaterIsolatedTest-$($Upgrade.Trim('{}'))" /></StandardDirectory>
$fail
</Package><Fragment><ComponentGroup Id="Payload" Directory="INSTALLFOLDER">
<Component Id="Exe" Guid="*"><File Source="$folder/Vex.exe" /></Component>
<Component Id="Helper" Guid="*"><File Source="$folder/vex-update.exe" /></Component>
<Component Id="VersionFile" Guid="*"><File Source="$folder/fixture-version.txt" /></Component>
<Component Id="Padding" Guid="*"><File Source="$Root/padding.bin" /></Component>
</ComponentGroup></Fragment></Wix>
"@
        [IO.File]::WriteAllText((Join-Path $folder 'Package.wxs'), $wxs)
        [IO.File]::WriteAllText((Join-Path $folder 'Fixture.wixproj'), '<Project Sdk="WixToolset.Sdk/5.0.2"><PropertyGroup><OutputName>Fixture</OutputName><InstallerPlatform>x64</InstallerPlatform></PropertyGroup></Project>')
        Run-Tool dotnet @('build',(Join-Path $folder 'Fixture.wixproj'),'-c','Release','--nologo')
        $Products += Get-ProductCode (Join-Path $folder 'bin/Release/Fixture.msi')
    }
    $initial = Start-Process msiexec.exe -ArgumentList @('/i',('"' + (Join-Path $Root '1.0.0/bin/Release/Fixture.msi') + '"'),'/qn','/norestart','ALLUSERS=2','MSIINSTALLPERUSER=1',('INSTALLFOLDER="' + $Install + '"')) -PassThru -Wait
    if ($initial.ExitCode -ne 0) { throw "Fixture installation failed: $($initial.ExitCode)" }
    Stage-Update '1.0.1'
    $Held = Start-Process (Join-Path $Install 'Vex.exe') -ArgumentList '--update-hold' -PassThru
    Invoke-Parent 'commit' 170
    if ($Held.HasExited) { throw 'Preflight killed another Vex instance' }
    $Held.Kill(); $Held.WaitForExit(); $Held.Dispose(); $Held = $null
    Write-Host 'PASS: another window rejects preflight without terminating it.'
    Invoke-Parent 'cancel' 0
    if ([IO.File]::ReadAllText((Join-Path $Install 'fixture-version.txt')) -ne '1.0.0') { throw 'Cancellation changed the installed version' }
    Write-Host 'PASS: acknowledged package is read-locked; cancellation leaves the installation untouched.'
    # Corruption after download must be rejected before the parent is asked to close.
    $msi = Join-Path $Stage 'update.msi'
    $stream = [IO.File]::Open($msi, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    $stream.Position = 100; $original = $stream.ReadByte()
    $stream.Position = 100; $stream.WriteByte($original -bxor 255); $stream.Dispose()
    Invoke-Parent 'commit' 23
    Write-Host 'PASS: staged package corruption rejects preflight.'
    Stage-Update '1.0.1'
    Invoke-Parent 'commit' 0
    Wait-Restart $false
    if (!(Test-Path (Join-Path $Stage 'update.msi'))) { throw 'Installed MSI source was not retained for repair' }
    if ($Installer.ProductState($Products[1]) -ne 5) { throw 'Updated MSI product is not registered' }
    Write-Host 'PASS: real MSI in-place upgrade, immediate parent exit, per-user scope, Unicode/spaced path and restart.'
    Remove-Item (Join-Path $Install 'restarted-1.0.1.marker')
    Stage-Update '1.0.2'
    Invoke-Parent 'commit' 0
    Wait-Restart $true
    if (!(Select-String -Path (Join-Path $Stage 'install.log') -SimpleMatch 'MainEngineThread is returning 1603' -Quiet)) { throw 'Expected failed MSI transaction was not logged' }
    if (!(Select-String -Path (Join-Path $Stage 'install.log') -SimpleMatch 'Executing op: FileCopy' -Quiet)) { throw 'Rollback fixture did not execute file copies before failing' }
    if ($Installer.ProductState($Products[1]) -ne 5) { throw 'Rollback did not restore MSI registration' }
    Write-Host 'PASS: failed MSI transaction restores the previous files, MSI registration and application restart.'
} finally {
    if ($Held) { if (!$Held.HasExited) { $Held.Kill(); $Held.WaitForExit() }; $Held.Dispose() }
    $cleanupFailed = $false
    foreach ($product in $Products) {
        $uninstall = Start-Process msiexec.exe -ArgumentList @('/x',$product,'/qn','/norestart') -PassThru -Wait
        if ($uninstall.ExitCode -notin @(0,1605)) { $cleanupFailed = $true; Write-Warning "Test product cleanup failed: $product ($($uninstall.ExitCode)); retaining recovery files" }
    }
    if (!$cleanupFailed -and (Test-Path $Install)) { Remove-Item $Install -Recurse -Force }
    Write-Host "Test-only UpgradeCode: $Upgrade; diagnostics retained in $Root"
}
if ($cleanupFailed) { throw 'Isolated MSI cleanup failed; recovery files were retained' }
