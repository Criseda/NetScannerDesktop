# Builds the downloadable NetScanner desktop release into artifacts/:
#
#   NetScanner-Setup-<version>-x64.exe     per-user installer (Inno Setup)
#   NetScanner-<version>-x64-portable.zip  unzip-and-run copy
#   SHA256SUMS.txt
#
# Both are self-contained (.NET runtime and Windows App SDK included), so
# users need nothing else installed. The version comes from <Version> in
# NetScannerDesktop.csproj; the engine is the pinned ns.exe (fetch-ns.ps1).
#
# Requires Visual Studio 2022+ (MSBuild with the Windows App SDK workload)
# and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#
# Usage:
#   pwsh -File Scripts/build-release.ps1

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "NetScannerDesktop\NetScannerDesktop.csproj"
$artifacts = Join-Path $root "artifacts"
$rid = "win-$($Platform.ToLowerInvariant())"
$publishDir = Join-Path $artifacts "publish\$rid"

$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $csproj." }
Write-Host "Building NetScanner $version ($rid)"

# Tools ------------------------------------------------------------------

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
}
if (-not $msbuild) {
    # vswhere can miss instances; fall back to the standard install folders, newest first.
    $msbuild = Get-ChildItem (Join-Path $env:ProgramFiles "Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe") -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName } -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $msbuild) { throw "MSBuild not found. Install Visual Studio with the Windows App SDK (WinUI) workload." }
Write-Host "MSBuild: $msbuild"

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

# Publish ----------------------------------------------------------------
# WindowsPackageType=None: build the unpackaged flavour (the project is
# otherwise set up for the MSIX package project). WindowsAppSDKSelfContained
# also switches off the csproj's bootstrapper, which must never run in a
# self-contained app (see WindowsAppSdkBootstrapInitialize in the csproj).

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
& $msbuild $csproj /restore /t:Publish /m /nologo /v:m `
    "/p:Configuration=$Configuration" "/p:Platform=$Platform" "/p:RuntimeIdentifier=$rid" `
    /p:SelfContained=true /p:WindowsAppSDKSelfContained=true `
    /p:WindowsPackageType=None `
    "/p:PublishDir=$publishDir\"
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

# Symbols stay out of what users download.
Get-ChildItem $publishDir -Recurse -Filter *.pdb | Remove-Item -Force

foreach ($required in "NetScannerDesktop.exe", "NetScannerDesktop.pri", "MainWindow.xbf", "Assets\Tools\ns.exe", "Microsoft.WindowsAppRuntime.dll") {
    if (-not (Test-Path (Join-Path $publishDir $required))) {
        throw "Publish output is missing $required; the app would not start."
    }
}

# Portable zip -------------------------------------------------------------

$zip = Join-Path $artifacts "NetScanner-$version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
# includeBaseDirectory=false, then wrap in a NetScanner\ folder so an
# "extract here" does not scatter files.
$stage = Join-Path $artifacts "stage"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage "NetScanner") | Out-Null
Copy-Item -Recurse "$publishDir\*" (Join-Path $stage "NetScanner")
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -Recurse -Force $stage

# Installer ----------------------------------------------------------------

& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$publishDir" "/DOutputDir=$artifacts" (Join-Path $root "Installer\NetScanner.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)." }
$setup = Join-Path $artifacts "NetScanner-Setup-$version-x64.exe"

# Checksums ----------------------------------------------------------------

$sums = foreach ($file in $setup, $zip) {
    $hash = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $file -Leaf)"
}
$sums | Set-Content -Encoding ascii (Join-Path $artifacts "SHA256SUMS.txt")

Write-Host ""
Write-Host "Release artifacts in $artifacts"
foreach ($file in $setup, $zip) {
    "{0,-45} {1,8:N1} MB" -f (Split-Path $file -Leaf), ((Get-Item $file).Length / 1MB) | Write-Host
}
$sums | Write-Host
