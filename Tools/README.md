# Tools (legacy)

Do not put `ns.exe` here anymore. The old checked-in binary (v0.3.0) went
stale and is gone.

The engine is now fetched at build time by `Scripts/fetch-ns.ps1` into
`NetScannerDesktop/Assets/Tools/ns.exe` (gitignored, shipped as app
Content so it also lands in the MSIX package). The pinned version lives
in `<NetScannerVersion>` in `NetScannerDesktop.csproj`.

This folder stays only to avoid breaking old paths; it can be deleted
once nothing references it.
