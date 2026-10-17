# Builds the release artifacts for Vex Native: a standalone win-x64 zip and an MSI installer.
#
# The installer installs Vex Native into Program Files, configures Desktop and Start Menu
# shortcuts, and registers uninstall cleanly in Windows.

[CmdletBinding()]
param(
    [string]$Version = "1.7.0",
    [string]$Configuration = "ReleaseSafe",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$NativeDir = $ScriptDir
$PublishDir = Join-Path $NativeDir "publish"
$AppPublishDir = Join-Path $PublishDir "app"
$SetupProj = Join-Path $NativeDir "installer\Vex.Native.Setup.wixproj"
$MsiSource = Join-Path $NativeDir "installer\bin\Release\VexSetup.msi"

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

$fourPartVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
$baseName = "Vex-Native-$Version-win-x64"
$zipPath = Join-Path $PublishDir "$baseName.zip"
$msiPath = Join-Path $PublishDir "$baseName.msi"

Write-Step "Building native binary (Zig 0.15.2, $Configuration)"
Push-Location $NativeDir
try {
    zig build "-Doptimize=$Configuration"
    if ($LASTEXITCODE -ne 0) { throw "zig build failed with exit code $LASTEXITCODE" }

    if (-not $SkipTests) {
        Write-Step "Running native test suite"
        zig build test
        if ($LASTEXITCODE -ne 0) { throw "zig build test failed with exit code $LASTEXITCODE" }
    }
}
finally {
    Pop-Location
}

Write-Step "Preparing publish staging directory"
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $AppPublishDir -Force | Out-Null

$BinDir = Join-Path $NativeDir "zig-out\bin"
$exeSource = Join-Path $BinDir "vex-native.exe"
$dllSource = Join-Path $BinDir "ghostty-vt.dll"
$icoSource = Join-Path $BinDir "app.ico"
$shellSource = Join-Path $BinDir "ShellIntegration"

if (-not (Test-Path $exeSource)) { throw "Missing expected binary at $exeSource" }
if (-not (Test-Path $dllSource)) { throw "Missing ghostty-vt.dll at $dllSource" }

# Stage files: provide both Vex.exe and vex-native.exe
Copy-Item $exeSource (Join-Path $AppPublishDir "Vex.exe") -Force
Copy-Item $exeSource (Join-Path $AppPublishDir "vex-native.exe") -Force
Copy-Item $dllSource (Join-Path $AppPublishDir "ghostty-vt.dll") -Force
if (Test-Path $icoSource) {
    Copy-Item $icoSource (Join-Path $AppPublishDir "app.ico") -Force
}
if (Test-Path $shellSource) {
    Copy-Item $shellSource (Join-Path $AppPublishDir "ShellIntegration") -Recurse -Force
}

Write-Step "Packing $baseName.zip"
$payload = Get-ChildItem $AppPublishDir -Recurse -File |
    Where-Object { $_.Extension -ne ".pdb" }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $payload) {
        $entry = $file.FullName.Substring($AppPublishDir.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $baseName.zip" | Set-Content "$zipPath.sha256" -NoNewline -Encoding ascii

Write-Step "Building WiX MSI installer"
$SetupDir = Join-Path $NativeDir "installer"
foreach ($stale in @("obj", "bin")) {
    $path = Join-Path $SetupDir $stale
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

dotnet build $SetupProj -c Release -p:Version="$fourPartVersion"
if ($LASTEXITCODE -ne 0) { throw "dotnet build Vex.Native.Setup.wixproj failed" }
if (-not (Test-Path $MsiSource)) { throw "MSI not produced at $MsiSource" }
Copy-Item $MsiSource $msiPath -Force

$msiHash = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$msiHash  $baseName.msi" | Set-Content "$msiPath.sha256" -NoNewline -Encoding ascii

Write-Step "Verifying artifacts"
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$names = $archive.Entries | ForEach-Object { $_.FullName }
$archive.Dispose()

$required = @('Vex.exe', 'ghostty-vt.dll')
$missing = $required | Where-Object { $_ -notin $names }
if ($missing) { throw "zip is missing required files: $($missing -join ', ')" }

$msiFile = Get-Item $msiPath
if ($msiFile.Length -lt 1MB) {
    throw "MSI is unexpectedly small ($($msiFile.Length) bytes); check WiX harvesting"
}

Write-Step "Done! Release artifacts generated at:"
Get-ChildItem $PublishDir -File | ForEach-Object {
    Write-Host ("    {0,-40} ({1,6:N2} MB)" -f $_.Name, ($_.Length / 1MB))
}
