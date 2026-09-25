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
#   powershell -File Scripts/fetch-ns.ps1 -Version v1.2.2 -Force
#   powershell -File Scripts/fetch-ns.ps1 -ZipPath ..\NetScanner\zig-out\releases\windows.zip
#
# Output:
#   NetScannerDesktop/Assets/Tools/ns.exe (gitignored, copied to build output)

param(
    # NetScanner release tag to download. Defaults to <NetScannerVersion>
    # in NetScannerDesktop.csproj, the single place the version is pinned.
    [string]$Version,

    # Where ns.exe should land. Defaults to the in-project Assets folder
    # so MSBuild can pick it up as Content.
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\NetScannerDesktop\Assets\Tools"),

    # Re-download even if the right version is already present.
    [switch]$Force,

    # Install from a local windows.zip instead of downloading, e.g. a
    # release built with `zig build release` before it is published on
    # GitHub. Same SHA-256 check as a download: pin the zip's hash first.
    [string]$ZipPath
)

$ErrorActionPreference = "Stop"

if (-not $Version) {
    $csproj = Join-Path $PSScriptRoot "..\NetScannerDesktop\NetScannerDesktop.csproj"
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.NetScannerVersion | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <NetScannerVersion> found in $csproj." }
}

# Single Windows asset published by NetScanner releases.
# Example: https://github.com/Criseda/NetScanner/releases/download/v1.0.0/windows.zip
$DownloadUrl = "https://github.com/Criseda/NetScanner/releases/download/$Version/windows.zip"

# SHA-256 of windows.zip, from the GitHub release API. Update this when
# bumping $Version. Download fails closed when the hash does not match.
$ExpectedSha256ByVersion = @{
    "v1.0.0" = "ecbc843dcc37942bf1b28ecaa111b7e6c120aee840899a8ea0077344dfc74263"
    "v1.1.0" = "d6e778bff05fb5c7db55313488a5df3fe3b56105e2e8daab4070f29f5a092c84"
    "v1.2.2" = "0b843db6e39f24849b6ef47302f7877ccd396022fab500378156445ba716b264"
    "v1.3.0" = "ff857ac662742996a71471c74b3a4e6275dcda7dbc913239cef27a38b9f21e1b"
    "v1.4.0" = "4636bfb26b6bff26f111bddc1dabb4e5377f8b2f08c475be6de3905688c97222"
}

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

if (-not $ExpectedSha256ByVersion.ContainsKey($Version)) {
    throw "No SHA-256 pinned for NetScanner $Version. Add it to `$ExpectedSha256ByVersion in $($MyInvocation.MyCommand.Path)."
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
    $expectedHash = $ExpectedSha256ByVersion[$Version].ToLowerInvariant()
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
