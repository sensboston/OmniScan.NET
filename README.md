# OmniScan

[![build](https://github.com/sensboston/OmniScan.NET/actions/workflows/build.yml/badge.svg)](https://github.com/sensboston/OmniScan.NET/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/sensboston/OmniScan.NET)](https://github.com/sensboston/OmniScan.NET/releases/latest)
[![license](https://img.shields.io/github/license/sensboston/OmniScan.NET)](LICENSE)

A small, fast Windows app for scanning documents and photos — and for sharing a USB scanner
with the other PCs on your home network.

**[Download OmniScan.exe](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan.exe)**
— one portable file, nothing to install, no .NET runtime needed. Windows 10/11, 64-bit.

<!-- Screenshots go here. -->

## Features

- **One window, instant start.** Scanners are found in the background; the window never waits for them.
- **Photos:** several photos on the glass are found, cropped, straightened and saved as separate files.
  Frames are shown on the preview and can be moved, resized, added or removed.
- **Documents:** margins removed, skew and page orientation (0/90/180/270) corrected.
- **Multi-page PDF** with a countdown card between pages, an insertion marker, rescan-in-place,
  blank-page skip, and appending to the last saved PDF.
- **Day folders and safe names:** `Scans\2026-11-24\scan_001.jpg`; existing files are never overwritten.
- **Results strip:** rotate, delete or open every saved file right after the scan.
- **Scanner sharing on the LAN:** turn it on in the settings of the PC the scanner is attached to.
  OmniScan then runs in the tray, starts with Windows, and its scanners appear on every other PC
  running OmniScan, as if they were attached locally. Processing always happens on the PC where you sit.
- **Portable settings** in a plain `OmniScan.ini` next to the executable
  (or in `%AppData%\OmniScan` if that folder is read-only).
- **29 UI languages**, light / dark / system theme.

## First start

The executable is not code-signed yet, so Windows SmartScreen may show "Windows protected your PC".
Click **More info** → **Run anyway**.

With scanner sharing turned on, Windows asks once whether to let OmniScan use the network:
allow it for private networks. The server answers only computers on the local network.

## How it works

- Scanning goes through **WIA 2.0**, so any scanner with a Windows driver works.
- Image processing is fully managed code, in memory, built on
  [Leptonica.Managed](https://github.com/sensboston/Leptonica.Managed).
- Sharing uses UDP broadcast for discovery and plain HTTP for scans (raw lossless frames, gzip).

Design notes: [docs/DESIGN.md](docs/DESIGN.md).

## Building from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build OmniScan.Net.slnx -c Release
dotnet test tests/OmniScan.Net.Tests -c Release

# The single portable OmniScan.exe:
dotnet publish src/OmniScan.Net.App -c Release -r win-x64 -o publish
```

## License

[MIT](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
