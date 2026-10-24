# ZIP staging is cross-platform (tools/package.py); WiX/MSI runs only on Windows.
[CmdletBinding()]
param(
    [string]$Version = '',
    [ValidateSet('Debug', 'ReleaseSafe')][string]$Configuration = 'ReleaseSafe',
    [string]$Label = '',
    [string]$BinDirectory = "$PSScriptRoot\..\zig-out\bin",
    [string]$Python = 'python',
    [switch]$SkipBuild,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'MSI packaging requires Windows. For a portable ZIP in WSL use: python3 app/tools/package.py'
}
$AppDir = Split-Path $PSScriptRoot -Parent
$expectedVersion = (Get-Content "$AppDir/VERSION" -Raw).Trim()
if (!$Version) { $Version = $expectedVersion }
if ($Version -ne $expectedVersion) { throw "Version must match app/VERSION ($expectedVersion)." }
if ($Label -and $Label -notmatch '^preview-[0-9a-f]{7,40}$') { throw 'Invalid preview label.' }
$PublishDir = Join-Path $AppDir 'publish'
$SetupDir = Join-Path $AppDir 'packages/windows'
$PackageTool = Join-Path $PSScriptRoot 'package.py'
$baseName = "Vex-Native-$Version$(if ($Label) { '-' + $Label })-win-x64"
$msiPath = Join-Path $PublishDir "$baseName.msi"

if (!$SkipBuild) {
    $zigVersion = & zig version
    if ($LASTEXITCODE -ne 0 -or $zigVersion.Trim() -ne (Get-Content "$AppDir/.zig-version" -Raw).Trim()) {
        throw 'Use the pinned Zig version, or -SkipBuild with binaries cross-compiled in Nix/WSL.'
    }
    Push-Location $AppDir
    try {
        zig build "-Doptimize=$Configuration"
        if ($LASTEXITCODE -ne 0) { throw 'zig build failed.' }
        if (!$SkipTests) {
            zig build test "-Doptimize=$Configuration"
            if ($LASTEXITCODE -ne 0) { throw 'zig build test failed.' }
        }
    } finally { Pop-Location }
} elseif (!$SkipTests) {
    & (Join-Path $BinDirectory 'vex-native-tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Compiled Zig tests failed.' }
}

Write-Host '==> Preparing and verifying portable ZIP'
$packageArgs = @($PackageTool, '--bin-dir', $BinDirectory, '--output-dir', $PublishDir, '--version', $Version)
if ($Label) { $packageArgs += @('--label', $Label) }
& $Python @packageArgs
if ($LASTEXITCODE -ne 0) { throw 'Portable packaging failed.' }

Write-Host '==> Building WiX MSI from the same staged payload'
foreach ($stale in @('obj', 'bin')) {
    $path = Join-Path $SetupDir $stale
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}
dotnet build (Join-Path $SetupDir 'Vex.Native.Setup.wixproj') -c Release "-p:Version=$Version.0"
if ($LASTEXITCODE -ne 0) { throw 'WiX MSI build failed.' }
$msiSource = Join-Path $SetupDir 'bin/Release/VexSetup.msi'
if (!(Test-Path $msiSource)) { throw "MSI not produced at $msiSource" }
Copy-Item $msiSource $msiPath -Force
$hash = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$msiPath.sha256", "$hash  $baseName.msi`n", [Text.Encoding]::ASCII)
& $Python @packageArgs --verify --require-msi
if ($LASTEXITCODE -ne 0) { throw 'Release payload or checksum verification failed.' }

# Administrative extraction checks the real cabinet payload, without installing or registering the application.
Write-Host '==> Verifying MSI extraction against staged files'
$verification = Join-Path ([IO.Path]::GetTempPath()) ('Vex-MSI-' + [Guid]::NewGuid().ToString('N'))
$log = Join-Path $PublishDir 'msi-verification.log'
try {
    New-Item -ItemType Directory -Force $verification | Out-Null
    $arguments = @('/a', ('"' + $msiPath + '"'), '/qn', ('TARGETDIR="' + $verification + '"'), '/l*v', ('"' + $log + '"'))
    $process = Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "MSI extraction failed ($($process.ExitCode)); see $log" }
    $executables = @(Get-ChildItem $verification -Recurse -File -Filter Vex.exe)
    if ($executables.Count -ne 1) { throw 'MSI must extract exactly one Vex.exe.' }
    $installRoot = $executables[0].Directory.FullName
    $staging = Join-Path $PublishDir 'app'
    foreach ($source in Get-ChildItem $staging -Recurse -File) {
        $relative = $source.FullName.Substring($staging.Length + 1)
        $extracted = Join-Path $installRoot $relative
        if (!(Test-Path $extracted) -or (Get-FileHash $source.FullName).Hash -ne (Get-FileHash $extracted).Hash) {
            throw "MSI payload differs from staging: $relative"
        }
    }
} finally {
    if (Test-Path $verification) { Remove-Item $verification -Recurse -Force }
}
if (!$Label) {
    & $Python @packageArgs --verify --require-msi --update-manifest
    if ($LASTEXITCODE -ne 0) { throw 'Update manifest generation failed.' }
}
Write-Host '==> Verified artifacts'
Get-ChildItem $PublishDir -File | Where-Object { $_.Extension -ne '.log' } | Select-Object Name, Length
