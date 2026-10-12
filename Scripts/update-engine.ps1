# Moves the bundled NetScanner engine to its latest release (or -Tag).
#
# Rewrites Directory.Build.props in place:
#   <NetScannerVersion>  the engine release tag
#   <NetScannerSha256>   SHA-256 of that release's windows.zip, from the
#                        asset digest GitHub publishes
#   <Version>            the app version, since a new engine ships as a new
#                        app release: an engine patch bumps the app's patch,
#                        anything bigger bumps the app's minor. Skipped when
#                        the current app version has not been released yet
#                        (no v<Version> tag), so two engine updates before a
#                        release do not skip an app version.
#
# Then downloads the engine once through fetch-ns.ps1, so the new pin is
# proven to match before anyone commits it.
#
# Usage:
#   pwsh -File Scripts/update-engine.ps1              # latest release
#   pwsh -File Scripts/update-engine.ps1 -Tag v1.7.0
#
# In GitHub Actions it writes updated/engine/app to $GITHUB_OUTPUT.

param(
    [string]$Tag
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$props = Join-Path $root "Directory.Build.props"

function Write-Output-Var([string]$name, [string]$value) {
    if ($env:GITHUB_OUTPUT) { "$name=$value" | Out-File -Append -Encoding utf8 $env:GITHUB_OUTPUT }
}

function Get-Prop([string]$text, [string]$name) {
    $m = [regex]::Match($text, "<$name>([^<]*)</$name>")
    if (-not $m.Success) { throw "No <$name> in $props." }
    return $m.Groups[1].Value.Trim()
}

function Set-Prop([string]$text, [string]$name, [string]$value) {
    return [regex]::Replace($text, "<$name>[^<]*</$name>", "<$name>$value</$name>")
}

function ConvertTo-Version([string]$tag) {
    $m = [regex]::Match($tag, '^v?(\d+)\.(\d+)\.(\d+)$')
    if (-not $m.Success) { throw "Not a release version: '$tag'." }
    return [version]::new([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value)
}

$text = [System.IO.File]::ReadAllText($props)
$pinned = Get-Prop $text "NetScannerVersion"
$appVersion = Get-Prop $text "Version"

$headers = @{ "User-Agent" = "NetScannerDesktop"; "Accept" = "application/vnd.github+json" }
if ($env:GITHUB_TOKEN) { $headers["Authorization"] = "Bearer $env:GITHUB_TOKEN" }
$api = "https://api.github.com/repos/Criseda/NetScanner/releases/" + $(if ($Tag) { "tags/$Tag" } else { "latest" })
$release = Invoke-RestMethod -Uri $api -Headers $headers

$latest = $release.tag_name
if ((ConvertTo-Version $latest) -le (ConvertTo-Version $pinned)) {
    Write-Host "Engine $pinned is current (latest release: $latest)."
    Write-Output-Var "updated" "false"
    return
}

$asset = $release.assets | Where-Object { $_.name -eq "windows.zip" } | Select-Object -First 1
if (-not $asset) { throw "Release $latest has no windows.zip." }
if ("$($asset.digest)" -notmatch '^sha256:([0-9a-fA-F]{64})$') {
    throw "Release $latest publishes no SHA-256 digest for windows.zip, so it cannot be pinned."
}
$sha = $Matches[1].ToLowerInvariant()

# Bump the app only if its current version already shipped.
$released = git -C $root ls-remote --tags origin "refs/tags/v$appVersion"
if ($LASTEXITCODE -ne 0) { throw "Could not list tags on origin." }
$newAppVersion = $appVersion
if ($released) {
    $old = ConvertTo-Version $pinned
    $new = ConvertTo-Version $latest
    $app = ConvertTo-Version $appVersion
    $newAppVersion = if ($new.Major -eq $old.Major -and $new.Minor -eq $old.Minor) {
        "$($app.Major).$($app.Minor).$($app.Build + 1)"
    } else {
        "$($app.Major).$($app.Minor + 1).0"
    }
}

$text = Set-Prop $text "NetScannerVersion" $latest
$text = Set-Prop $text "NetScannerSha256" $sha
$text = Set-Prop $text "Version" $newAppVersion
[System.IO.File]::WriteAllText($props, $text)

# Prove the new pin: download and checksum-verify the engine now.
& (Join-Path $PSScriptRoot "fetch-ns.ps1") -Version $latest -Sha256 $sha -Force

Write-Host "Engine $pinned -> $latest, app $appVersion -> $newAppVersion."
Write-Output-Var "updated" "true"
Write-Output-Var "engine" $latest
Write-Output-Var "previous_engine" $pinned
Write-Output-Var "app" $newAppVersion
