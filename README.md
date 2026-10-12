# NetScannerDesktop

WinUI 3 frontend for [NetScanner](https://github.com/Criseda/NetScanner)
(the Zig LAN scanner). No scanning logic lives here — the app runs the
`ns` engine and shows its results in a Windows 11 style UI: find live
hosts (optionally with hostnames, MAC addresses and manufacturers), then
scan any of them for open ports.

## Download

Get the latest from [Releases](https://github.com/Criseda/NetScannerDesktop/releases/latest):

- **`NetScanner-Setup-<version>-x64.exe`** (recommended): installs for your
  user account only (no admin prompt), adds a Start menu entry, upgrades in
  place, and uninstalls from Settings > Apps.
- **`NetScanner-<version>-x64-portable.zip`**: extract anywhere and run
  `NetScanner\NetScannerDesktop.exe`.

Both include everything they need (no .NET or Windows App Runtime install)
and run on Windows 10 1809+ and Windows 11, x64 or ARM64 (via emulation).
The downloads are not code-signed yet, so Windows SmartScreen may show
"Windows protected your PC": choose **More info > Run anyway**. Checksums
are in `SHA256SUMS.txt` on each release.

To build a release yourself: `pwsh -File Scripts/build-release.ps1`
(needs Visual Studio with the Windows App SDK workload and
[Inno Setup 6](https://jrsoftware.org/isinfo.php)).

## Engine binary (not in git)

`ns.exe` is downloaded at build time, never committed:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1
```

This fetches the pinned release into `NetScannerDesktop/Assets/Tools/`,
checksum-verified, and ships it as app Content so it also lands in the
MSIX package. The build runs the script automatically.

To use an engine that is built but not yet published (e.g. from a
NetScanner PR), install its zip locally with its version and SHA-256:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1 -ZipPath ..\NetScanner\zig-out\releases\windows.zip -Version v1.7.0 -Sha256 <hash> -Force
```

The app only ever runs the `ns.exe` it ships with. It never downloads
code, so a new engine always reaches users as a new app release (which is
also what the Microsoft Store requires).

## Versions and releases

Every version lives in [`Directory.Build.props`](Directory.Build.props),
and nothing else restates them:

- `Version`: the app. Stamped into the exe, the MSIX manifest (leave
  `Package.appxmanifest` at `0.0.0.0`), the installer and the file names.
- `NetScannerVersion` + `NetScannerSha256`: the bundled engine and the
  SHA-256 of its release `windows.zip`. The build refuses a mismatch.
- The Windows App SDK, Windows SDK build tools and MVVM Toolkit versions
  shared by the app, package and test projects.

How a release happens:

1. **Engine update** (daily, or run it from the Actions tab) finds a new
   NetScanner release and opens a PR that pins it and bumps `Version`
   (engine patch → app patch, otherwise app minor). Locally:
   `pwsh -File Scripts/update-engine.ps1`.
2. Merging any change to `Version` on master runs **Release**, which builds
   the installer and portable zip and creates a draft GitHub release.
3. Write the notes in the draft and publish it (this creates the tag).
4. Publishing runs **Store package**, which builds
   `NetScanner-<version>.msixupload` (x64 + ARM64) as a workflow artifact.
   Upload it to a new submission in Partner Center. Locally:
   `pwsh -File Scripts/build-store.ps1`.

## Microsoft Store

The Store build is the MSIX package project, not the installer: the Store
signs it, delivers updates, and installs the Windows App Runtime it
depends on. Its identity (`Name`, `Publisher` and `PublisherDisplayName` in
`Package.appxmanifest`) must match the app reserved in Partner Center
(Product management > Product identity). Until it does, `build-store.ps1`
warns and Partner Center rejects the upload.

To try the packaged app locally (Developer Mode on), build the package
project and register its layout:

```powershell
Add-AppxPackage -Register "NetScannerDesktop (Package)\bin\x64\Release\AppxManifest.xml"
```

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

Tests (parser, validation, list view, CSV) run without Visual
Studio:

```powershell
dotnet test Tests/NetScannerDesktop.Tests
```

The Windows App SDK package is pinned (in `Directory.Build.props`) to the
release that matches the widely installed 1.8 runtime (8000.946). Newer 1.8
builds refuse to start unpackaged on machines that only have that runtime,
so bump it deliberately.

## Layout

- `Directory.Build.props` — app, engine and shared package versions
- `Scripts/fetch-ns.ps1` — downloads the pinned `ns.exe`
- `Scripts/update-engine.ps1` — pins the latest engine release
- `Scripts/build-release.ps1` — installer and portable zip
- `Scripts/build-store.ps1` — Microsoft Store upload (`.msixupload`)
- `.github/workflows/` — CI, Engine update, Release, Store package
- `NetScannerDesktop/Models/` — hosts, host details, ports, scan summaries
- `NetScannerDesktop/Services/` — engine runner (`NetScannerService`), output
  parser (`EngineOutputParser` in `NetScannerParser.cs`), validation, settings/history storage, CSV export, `FilteredSortedView`
  and `TableSort` (the sortable result tables)
- `NetScannerDesktop/ViewModels/` — `ScanViewModelBase` (shared scan
  plumbing), `DiscoveryViewModel`, `PortScanViewModel`, `SettingsViewModel`
- `NetScannerDesktop/Views/` — `DiscoveryPage`, `PortScanPage`,
  `SettingsPage` (includes About), `PageHelpers`, `WrapPanel` (scan toolbars)
- `Tests/NetScannerDesktop.Tests/` — xUnit tests for the WinUI-free code

App data lives in `%LOCALAPPDATA%\NetScanner\` (`settings.json`,
`history.json`); crash details go to `%TEMP%\NetScannerDesktop.crash.log`.
