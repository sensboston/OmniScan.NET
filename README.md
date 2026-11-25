# OmniScan

[![build](https://github.com/sensboston/OmniScan.NET/actions/workflows/build.yml/badge.svg)](https://github.com/sensboston/OmniScan.NET/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/sensboston/OmniScan.NET)](https://github.com/sensboston/OmniScan.NET/releases/latest)
[![license](https://img.shields.io/github/license/sensboston/OmniScan.NET)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)

A small, fast Windows app for scanning documents and photos — and for sharing a USB scanner
with the other PCs on your home network.

**[Download OmniScan.exe](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan.exe)**
(~74 MB) — one portable file, nothing to install, no .NET runtime needed. Windows 10 (1809+) or 11, 64-bit.

Already have the [.NET 10 Desktop Runtime](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe)? Then the
**[lite build](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan-lite.exe)**
(~35 MB) is the same program without the runtime inside.

<!-- Screenshots go here. -->

## Features

- **One window, instant start.** Scanners are found in the background; the window never waits for them.
- **Photos:** several photos on the glass are found, cropped, straightened and saved as separate files.
  Frames are shown on the preview and can be moved, resized, added or removed.
- **Photos laid any old way** — tilted, close together, with white skies running into the lid — are still
  cut out one by one.
- **Documents:** margins removed, skew and page orientation (0/90/180/270) corrected. A JPEG or PNG scan
  that looks like a document offers to become a PDF, without scanning again.
- **Multi-page PDF** with a countdown card between pages, an insertion marker, rescan-in-place,
  blank-page skip, and appending to the last saved PDF.
- **Day folders and safe names:** `Scans\2026-11-24\scan_001.jpg`, one counter for all formats;
  existing files are never overwritten.
- **Results strip and viewer:** rotate, delete or open every saved file right after the scan; a click shows
  it over the whole window, with zoom.
- **Optional online help, with your own API key** (OpenAI, Google AI Studio, DeepSeek): photos turned
  upright, and a short description of every photo and PDF with words to find it by — in `descript.ion`
  (Total Commander, Far), in the photo's properties (Windows Search) and in the PDF's title.
  Off until you turn it on. What is sent, and what each service may do with it:
  [Online services](docs/ASSIST.md).
- **Scanner sharing on the LAN:** turn it on in the settings of the PC the scanner is attached to.
  OmniScan then runs in the tray, starts with Windows, and its scanners appear on every other PC
  running OmniScan, as if they were attached locally. Processing always happens on the PC where you sit.
- **Portable settings** in a plain `OmniScan.ini` next to the executable
  (or in `%AppData%\OmniScan` if that folder is read-only).
- **29 UI languages**, light and dark theme.

## First start

The executable is not code-signed yet, so Windows SmartScreen may show "Windows protected your PC".
Click **More info** → **Run anyway**.

With scanner sharing turned on, Windows asks once whether to let OmniScan use the network:
allow it for private networks. The server answers only computers on the local network.

## How it works

- Scanning goes through **WIA 2.0**, so any scanner with a Windows driver works.
- Image processing is fully managed code, in memory, built on
  [Leptonica.Managed](https://github.com/sensboston/Leptonica.Managed). PDF pages are also read by the
  text recognition built into Windows, on the computer, to turn them upright.
- Sharing uses UDP broadcast for discovery and plain HTTP for scans (raw lossless frames, gzip).

Design notes: [docs/DESIGN.md](docs/DESIGN.md).

## Building from source

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build OmniScan.Net.slnx -c Release
dotnet test tests/OmniScan.Net.Tests -c Release

# The single portable OmniScan.exe (runtime inside):
dotnet publish src/OmniScan.Net.App -c Release -r win-x64 -o publish

# The lite OmniScan.exe (uses the installed .NET 10 Desktop Runtime):
dotnet publish src/OmniScan.Net.App -c Release -r win-x64 -p:Lite=true -o publish-lite
```

## License

[MIT](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
