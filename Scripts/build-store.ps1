# Builds the Microsoft Store upload into artifacts/store/:
#
#   NetScanner-<version>.msixupload   x64 + ARM64 bundle for Partner Center
#
# The Store signs the package and installs the Windows App Runtime it
# depends on, so the bundle is unsigned and not self-contained like the
# installer. The version comes from <Version> in Directory.Build.props;
# the package identity (Name, Publisher, PublisherDisplayName) from
# NetScannerDesktop (Package)/Package.appxmanifest, which must match the
# app reserved in Partner Center.
#
# Requires Visual Studio 2022+ with the Windows App SDK workload.
#
# Usage:
#   pwsh -File Scripts/build-store.ps1

param(
    [string]$Configuration = "Release",
    [string]$Platforms = "x64|arm64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$wapproj = Join-Path $root "NetScannerDesktop (Package)\NetScannerDesktop (Package).wapproj"
$manifest = Join-Path $root "NetScannerDesktop (Package)\Package.appxmanifest"
$out = Join-Path $root "artifacts\store"

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in Directory.Build.props." }

$identity = ([xml](Get-Content $manifest)).Package.Identity
Write-Host "Building Store package $($identity.Name) $version ($Platforms), publisher $($identity.Publisher)"
if ($identity.Publisher -notmatch '^CN=[0-9A-Fa-f-]{36}$') {
    Write-Warning "Publisher '$($identity.Publisher)' is not a Partner Center publisher ID; Partner Center will reject this upload until the identity in Package.appxmanifest matches the reserved app."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
}
if (-not $msbuild) {
    $msbuild = Get-ChildItem (Join-Path $env:ProgramFiles "Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe") -ErrorAction SilentlyContinue |
        Sort-Object { $_.FullName } -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $msbuild) { throw "MSBuild not found. Install Visual Studio with the Windows App SDK (WinUI) workload." }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$build = Join-Path $out "build"
& $msbuild $wapproj /restore /m /nologo /v:m `
    "/p:Configuration=$Configuration" /p:Platform=x64 `
    /p:UapAppxPackageBuildMode=StoreUpload /p:AppxBundle=Always "/p:AppxBundlePlatforms=$Platforms" `
    "/p:AppxPackageDir=$build\"
if ($LASTEXITCODE -ne 0) { throw "Store package build failed (exit $LASTEXITCODE)." }

$upload = Get-ChildItem $build -Filter *.msixupload | Select-Object -First 1
if (-not $upload) { throw "No .msixupload produced in $build." }
$target = Join-Path $out "NetScanner-$version.msixupload"
Move-Item $upload.FullName $target
Remove-Item -Recurse -Force $build

Write-Host ""
"{0}  {1:N1} MB" -f $target, ((Get-Item $target).Length / 1MB) | Write-Host
