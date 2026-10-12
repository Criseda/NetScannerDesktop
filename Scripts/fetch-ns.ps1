# Downloads the NetScanner engine (ns.exe) for Windows.
#
# Why this script exists:
# NetScannerDesktop is only a frontend. The real scanning lives in the
# Zig project Criseda/NetScanner. Instead of committing ns.exe to git
# (it went stale: the old Tools/ns.exe was stuck on v0.3.0), we
# download the matching release binary at build time.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1
#   powershell -File Scripts/fetch-ns.ps1 -Version v1.2.2 -Sha256 <hash> -Force
#   powershell -File Scripts/fetch-ns.ps1 -ZipPath ..\NetScanner\zig-out\releases\windows.zip
#
# Output:
#   NetScannerDesktop/Assets/Tools/ns.exe (gitignored, copied to build output)

param(
    # NetScanner release tag to download, and the SHA-256 of its
    # windows.zip. Both default to <NetScannerVersion> / <NetScannerSha256>
    # in Directory.Build.props, the single place the engine is pinned.
    [string]$Version,
    [string]$Sha256,

    # Where ns.exe should land. Defaults to the in-project Assets folder
    # so MSBuild can pick it up as Content.
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\NetScannerDesktop\Assets\Tools"),

    # Re-download even if the right version is already present.
    [switch]$Force,

    # Install from a local windows.zip instead of downloading, e.g. a
    # release built with `zig build release` before it is published on
    # GitHub. Same SHA-256 check as a download: pass the zip's hash.
    [string]$ZipPath
)

$ErrorActionPreference = "Stop"

if (-not $Version) {
    $props = Join-Path $PSScriptRoot "..\Directory.Build.props"
    $group = ([xml](Get-Content $props)).Project.PropertyGroup
    $Version = $group.NetScannerVersion | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <NetScannerVersion> found in $props." }
    if (-not $Sha256) { $Sha256 = $group.NetScannerSha256 | Where-Object { $_ } | Select-Object -First 1 }
}

# Single Windows asset published by NetScanner releases.
# Example: https://github.com/Criseda/NetScanner/releases/download/v1.0.0/windows.zip
$DownloadUrl = "https://github.com/Criseda/NetScanner/releases/download/$Version/windows.zip"

$ExePath = Join-Path $OutputDir "ns.exe"
$VersionFile = Join-Path $OutputDir "ns.version.txt"

function Get-InstalledVersion {
    if (-not (Test-Path $ExePath)) { return $null }
    try {
        $output = & $ExePath --version 2>$null
        if ($output -is [array]) { $output = $output -join "`n" }
        return "$output".Trim()
    } catch {
        return $null
    }
}

# Skip work when the right binary is already there.
if (-not $Force -and -not $ZipPath) {
    $installed = Get-InstalledVersion
    if ($installed -eq $Version -and (Test-Path $VersionFile)) {
        Write-Host "ns.exe $Version already present at $ExePath, skipping download."
        return
    }
    if ($installed) {
        Write-Host "Found ns.exe $installed, want $Version. Re-downloading."
    }
}

# Fail closed: nothing is extracted or run without a hash to check it against.
if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw "No valid SHA-256 for NetScanner $Version. Pass -Sha256, or set <NetScannerSha256> in Directory.Build.props."
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("netscanner-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
try {
    $tempZip = Join-Path $tempDir "windows.zip"
    # Plain .NET instead of Invoke-WebRequest / Get-FileHash / Expand-Archive:
    # when MSBuild runs from a PowerShell 7 terminal, the Windows PowerShell
    # that runs this script inherits pwsh's PSModulePath and cannot
    # autoload those cmdlets' modules.
    if ($ZipPath) {
        Write-Host "Installing NetScanner $Version from $ZipPath ..."
        Copy-Item -Path $ZipPath -Destination $tempZip
    } else {
        Write-Host "Downloading NetScanner $Version from $DownloadUrl ..."
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
        (New-Object System.Net.WebClient).DownloadFile($DownloadUrl, $tempZip)
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($tempZip)
    try {
        $actualHash = ([System.BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
    $expectedHash = $Sha256.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 mismatch for windows.zip. Expected $expectedHash, got $actualHash."
    }
    Write-Host "Checksum OK."

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($tempZip, (Join-Path $tempDir "out"))

    # The zip contains ns.exe at its root. Fail loudly if the layout changes.
    $downloadedExe = Get-ChildItem -Path $tempDir -Recurse -Filter "ns.exe" | Select-Object -First 1
    if (-not $downloadedExe) {
        throw "windows.zip did not contain ns.exe. Contents: $((Get-ChildItem $tempDir -Recurse | Select-Object -ExpandProperty FullName) -join ', ')"
    }

    Copy-Item -Path $downloadedExe.FullName -Destination $ExePath -Force
    Set-Content -Path $VersionFile -Value $Version -NoNewline

    $installedNow = Get-InstalledVersion
    Write-Host "Installed ns.exe ($installedNow) to $ExePath"
} finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
