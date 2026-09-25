# NetScannerDesktop

WinUI 3 frontend for [NetScanner](https://github.com/Criseda/NetScanner)
(the Zig LAN scanner). No scanning logic lives here — the app runs the
`ns` engine and shows its results in a Windows 11 style UI: find live
hosts (optionally with hostnames, MAC addresses and manufacturers), then
scan any of them for open ports.

## Engine binary (not in git)

`ns.exe` is downloaded at build time, never committed:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1
```

This fetches the pinned release into `NetScannerDesktop/Assets/Tools/`,
checksum-verified, and ships it as app Content so it also lands in the
MSIX package. The build runs the script automatically.

`<NetScannerVersion>` in `NetScannerDesktop.csproj` is the only place the
version is pinned; the script and the app both read it. To bump it, change
that value and add the release's `windows.zip` SHA-256 to
`$ExpectedSha256ByVersion` in `Scripts/fetch-ns.ps1`.

To use an engine that is built but not yet published (e.g. from a
NetScanner PR), install its zip locally; the pinned SHA-256 still applies:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1 -ZipPath ..\NetScanner\zig-out\releases\windows.zip
```

At runtime the app can also install a newer engine per-user (Discovery
offers it). Updates are SHA-256 checked against the GitHub release digest
before they run, limited to the pinned major version, and the app always
launches the newest compatible copy it has.

The app asks for `ns --json` output whenever the engine advertises it, and
falls back to reading the text output of v1.1–v1.2 engines.

## Develop

Open `NetScannerDesktop.sln` in Visual Studio (2022 17.13+ or newer, with
the .NET and Windows App SDK workloads) and press F5 — that builds the
app, fetches `ns.exe` if missing, and deploys the MSIX package.

Command line (Visual Studio's MSBuild, not plain `dotnet build`, which
lacks the Appx packaging tasks):

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" NetScannerDesktop.sln /p:Configuration=Debug /p:Platform=x64 /t:Build /m
```

Tests (parser, validation, list view, CSV, versions) run without Visual
Studio:

```powershell
dotnet test Tests/NetScannerDesktop.Tests
```

The Windows App SDK package is pinned to the release that matches the
widely installed 1.8 runtime (8000.946). Newer 1.8 builds refuse to start
unpackaged on machines that only have that runtime, so bump it deliberately.

## Layout

- `Scripts/fetch-ns.ps1` — downloads the pinned `ns.exe`
- `NetScannerDesktop/Models/` — hosts, host details, ports, scan summaries
- `NetScannerDesktop/Services/` — engine runner (`NetScannerService`), output
  parser (`EngineOutputParser` in `NetScannerParser.cs`), engine updater,
  validation, settings/history storage, CSV export, `FilteredSortedView`
- `NetScannerDesktop/ViewModels/` — `ScanViewModelBase` (shared scan
  plumbing), `DiscoveryViewModel`, `PortScanViewModel`, `SettingsViewModel`
- `NetScannerDesktop/Views/` — `DiscoveryPage`, `PortScanPage`,
  `SettingsPage` (includes About), `PageHelpers`
- `Tests/NetScannerDesktop.Tests/` — xUnit tests for the WinUI-free code

App data lives in `%LOCALAPPDATA%\NetScanner\` (`settings.json`,
`history.json`); crash details go to `%TEMP%\NetScannerDesktop.crash.log`.
