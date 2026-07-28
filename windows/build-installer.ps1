# Vex Terminal Workspace - Build & Package Script
# Generates a standalone self-contained release build and Windows Installer (.msi)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$RootDir = Split-Path $ScriptDir -Parent
$PublishDir = Join-Path $ScriptDir "publish"
$AppPublishDir = Join-Path $PublishDir "app"
$InstallerOutputDir = Join-Path $PublishDir "installer"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Building Vex Terminal Workspace Installer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Clean previous build artifacts
if (Test-Path $PublishDir) {
    Write-Host "[1/4] Cleaning previous publish artifacts..." -ForegroundColor Yellow
    Remove-Item -Path $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $AppPublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $InstallerOutputDir -Force | Out-Null

# 2. Publish Vex.App as self-contained win-x64 executable package
Write-Host "[2/4] Publishing Vex.App (win-x64 self-contained)..." -ForegroundColor Yellow
$AppProj = Join-Path $ScriptDir "Vex.App\Vex.App.csproj"
dotnet publish $AppProj -c Release -o $AppPublishDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
}

# 3. Build WiX MSI Installer package
Write-Host "[3/4] Building WiX MSI Installer package..." -ForegroundColor Yellow
$SetupProj = Join-Path $ScriptDir "Vex.Setup\Vex.Setup.wixproj"
dotnet build $SetupProj -c Release -p:TargetDir="$AppPublishDir\"
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build Vex.Setup.wixproj failed!"
}

# 4. Copy generated MSI to publish folder and Desktop
$MsiSource = Join-Path $ScriptDir "Vex.Setup\bin\Release\VexSetup.msi"
$DesktopTarget = "C:\Users\kacpe\Desktop\Vex.msi"

if (Test-Path $MsiSource) {
    $MsiTarget = Join-Path $InstallerOutputDir "Vex.msi"
    Copy-Item -Path $MsiSource -Destination $MsiTarget -Force
    Copy-Item -Path $MsiSource -Destination $DesktopTarget -Force
    Write-Host "========================================" -ForegroundColor Green
    Write-Host " SUCCESS! Installer generated at:" -ForegroundColor Green
    Write-Host " $MsiTarget" -ForegroundColor White
    Write-Host " Copied to Desktop:" -ForegroundColor Green
    Write-Host " $DesktopTarget" -ForegroundColor White
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Error "Installer file not found at expected location: $MsiSource"
}
