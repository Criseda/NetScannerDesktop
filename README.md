# NetScannerDesktop

WinUI 3 frontend for [NetScanner](https://github.com/Criseda/NetScanner)
(the Zig LAN scanner). No scanning logic lives here — the app runs the
`ns` engine and shows its results in a modern Windows UI.

## Engine binary (not in git)

`ns.exe` is downloaded at build time, never committed:

```powershell
powershell -ExecutionPolicy Bypass -File Scripts/fetch-ns.ps1
```

This fetches the pinned release (see `<NetScannerVersion>` in
`NetScannerDesktop.csproj`, checksum-verified) into
`NetScannerDesktop/Assets/Tools/`, which is shipped as app Content so it
also lands in the MSIX package. The build auto-runs the script when the
binary is missing.

## Develop

Open `NetScannerDesktop.sln` in Visual Studio (2022 17.13+ or newer, with
the .NET and Windows App SDK workloads) and press F5 — that builds the
app, fetches `ns.exe` if missing, and deploys the MSIX package.

Command line (Visual Studio's MSBuild, not plain `dotnet build`, which
lacks the Appx packaging tasks):

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" NetScannerDesktop.sln /p:Configuration=Debug /p:Platform=x64 /t:Build /m
```

Pages are MVVM (`CommunityToolkit.Mvvm`): `Views/` + `ViewModels/`,
engine access in `Services/NetScannerService.cs`, output parsing in
`Services/NetScannerParser.cs`.

## Layout

- `Scripts/fetch-ns.ps1` — downloads the pinned `ns.exe`
- `NetScannerDesktop/Models/` — scan result records
- `NetScannerDesktop/Services/` — engine runner, parser, validation, subnets, export
- `NetScannerDesktop/ViewModels/` — `DiscoveryViewModel`, `PortScanViewModel`
- `NetScannerDesktop/Views/` — `DiscoveryPage`, `PortScanPage`
- `Tools/` — legacy folder, kept only as a pointer (do not add binaries)
