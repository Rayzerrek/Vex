# Builds the release artifacts for Vex: a self-contained win-x64 zip and an MSI.
#
# The zip must contain every file `dotnet publish` produces except debug
# symbols. Native WPF DLLs (wpfgfx_cor3, PresentationNative_cor3, ...) live
# beside the exe rather than inside the single-file bundle, so omitting them
# produces an executable that throws DllNotFoundException on first paint.

$ErrorActionPreference = "Stop"

$ScriptDir = $PSScriptRoot
$PublishDir = Join-Path $ScriptDir "publish"
$AppPublishDir = Join-Path $PublishDir "app"
$AppProj = Join-Path $ScriptDir "Vex.App\Vex.App.csproj"
$SetupProj = Join-Path $ScriptDir "Vex.Setup\Vex.Setup.wixproj"
$MsiSource = Join-Path $ScriptDir "Vex.Setup\bin\Release\VexSetup.msi"

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Read the version from the csproj so it has one source of truth; AppInfo
# resolves the same property from the built assembly.
Write-Step "Reading version"
$version = ([xml](Get-Content $AppProj)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $AppProj" }
Write-Host "    version $version"

$baseName = "Vex-$version-win-x64"
$zipPath = Join-Path $PublishDir "$baseName.zip"
$msiPath = Join-Path $PublishDir "$baseName.msi"

if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Path $AppPublishDir -Force | Out-Null

# WiX caches the harvested payload under the setup project's obj directory and
# reuses it when only the publish output changed, producing an MSI with no
# files in it. Clearing that cache forces a real re-harvest.
$SetupDir = Join-Path $ScriptDir "Vex.Setup"
foreach ($stale in @("obj", "bin")) {
    $path = Join-Path $SetupDir $stale
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Write-Step "Publishing Vex.App (win-x64, self-contained)"
dotnet publish $AppProj -c Release -o $AppPublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Step "Packing $baseName.zip"
# Symbols make the archive several times larger and are of no use to someone
# running a release build. Everything else is required, native DLLs included.
# Packing happens before the MSI build, which drops its own .wixpdb and .msi
# into the publish directory.
$payload = Get-ChildItem $AppPublishDir -Recurse -File |
    Where-Object { $_.Extension -ne ".pdb" }
Compress-Archive -Path $payload.FullName -DestinationPath $zipPath -Force

Write-Step "Building MSI"
dotnet build $SetupProj -c Release -p:TargetDir="$AppPublishDir\"
if ($LASTEXITCODE -ne 0) { throw "dotnet build Vex.Setup.wixproj failed" }
if (-not (Test-Path $MsiSource)) { throw "MSI not produced at $MsiSource" }
Copy-Item $MsiSource $msiPath

Write-Step "Writing checksum"
# Filename only: `sha256sum -c` and `Get-FileHash` both need a path relative to
# the checksum file. An absolute path from the build machine is unusable.
$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $baseName.zip" | Set-Content "$zipPath.sha256" -NoNewline -Encoding ascii

Write-Step "Done"
Get-ChildItem $PublishDir -File | ForEach-Object {
    Write-Host ("    {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB))
}
