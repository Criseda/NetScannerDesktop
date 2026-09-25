# Downloads the NetScanner engine (ns.exe) for Windows.
#
# Why this script exists:
# NetScannerDesktop is only a frontend. The real scanning lives in the
# Zig project Criseda/NetScanner. Instead of committing ns.exe to git
# (it goes stale, see old Tools/ns.exe v0.3.0 vs current v1.1.0), we
# download the matching release binary at build time.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1
#   powershell -File Scripts/fetch-ns.ps1 -Version v1.1.0 -Force
#
# Output:
#   NetScannerDesktop/Assets/Tools/ns.exe (gitignored, copied to build output)

param(
    # NetScanner release tag to download. Keep in sync with
    # <NetScannerVersion> in NetScannerDesktop.csproj.
    [string]$Version = "v1.1.0",

    # Where ns.exe should land. Defaults to the in-project Assets folder
    # so MSBuild can pick it up as Content.
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\NetScannerDesktop\Assets\Tools"),

    # Re-download even if the right version is already present.
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# Single Windows asset published by NetScanner releases.
# Example: https://github.com/Criseda/NetScanner/releases/download/v1.0.0/windows.zip
$DownloadUrl = "https://github.com/Criseda/NetScanner/releases/download/$Version/windows.zip"

# SHA-256 of windows.zip, from the GitHub release API. Update this when
# bumping $Version. Download fails closed when the hash does not match.
$ExpectedSha256ByVersion = @{
    "v1.0.0" = "ecbc843dcc37942bf1b28ecaa111b7e6c120aee840899a8ea0077344dfc74263"
    "v1.1.0" = "d6e778bff05fb5c7db55313488a5df3fe3b56105e2e8daab4070f29f5a092c84"
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
if (-not $Force) {
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
    $zipPath = Join-Path $tempDir "windows.zip"
    Write-Host "Downloading NetScanner $Version from $DownloadUrl ..."
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $zipPath

    $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $ExpectedSha256ByVersion[$Version].ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "SHA-256 mismatch for windows.zip. Expected $expectedHash, got $actualHash."
    }
    Write-Host "Checksum OK."

    Expand-Archive -Path $zipPath -DestinationPath $tempDir -Force

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
