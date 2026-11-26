# OmniScan

[![build](https://github.com/sensboston/OmniScan.NET/actions/workflows/build.yml/badge.svg)](https://github.com/sensboston/OmniScan.NET/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/sensboston/OmniScan.NET)](https://github.com/sensboston/OmniScan.NET/releases/latest)
[![license](https://img.shields.io/github/license/sensboston/OmniScan.NET)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)

A small, fast Windows app for flatbed scanners: documents, photos, multi-page PDF, stickers — and a USB
scanner shared with every PC on your home network.

**[Download OmniScan.exe](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan.exe)**
(~68 MB) — one portable file, nothing to install, no .NET runtime needed. Windows 10 (1809+) or 11, 64-bit.

Already have the [.NET 10 Desktop Runtime](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe)? Then the
**[lite build](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan-lite.exe)**
(~10 MB) is the same program without the runtime inside.

## Screenshots

<p align="center">
  <a href="https://github.com/user-attachments/assets/7db07fd7-f8ed-47ca-8ff1-8ae0528eee87"><img src="https://github.com/user-attachments/assets/7db07fd7-f8ed-47ca-8ff1-8ae0528eee87" width="49%" alt="Photos found on the glass, cut out and saved"></a>
  <a href="https://github.com/user-attachments/assets/23c3d83b-6f6c-4e53-a8c1-f11ee70fd6bd"><img src="https://github.com/user-attachments/assets/23c3d83b-6f6c-4e53-a8c1-f11ee70fd6bd" width="49%" alt="Stickers outlined on the preview"></a>
</p>
<p align="center">
  <a href="https://github.com/user-attachments/assets/1450b850-7575-490f-9924-60aed87455c1"><img src="https://github.com/user-attachments/assets/1450b850-7575-490f-9924-60aed87455c1" width="49%" alt="A scan that looks like a document offers PDF"></a>
  <a href="https://github.com/user-attachments/assets/242e2d19-7d2a-4ca6-94b5-b199fa467894"><img src="https://github.com/user-attachments/assets/242e2d19-7d2a-4ca6-94b5-b199fa467894" width="49%" alt="A PDF page, with its description for search"></a>
</p>

## What makes it different

- **Share a USB scanner with the whole home.** Turn on sharing on the PC the scanner is plugged into, and
  OmniScan on every other PC lists that scanner as if it were its own — no drivers, no setup, no
  accounts. While someone is scanning at that PC, a scan from another one is refused at once (it never
  takes whatever lies on the glass), the scanner list shows a green or red dot, and the other PC can
  ask for the scanner with one button.
- **Stickers cut out along their outline.** Put stickers on the glass, and OmniScan cuts each one along
  its shape with a clean white border, onto a transparent background (PNG). It notices stickers by
  itself and offers the mode; the preview then shows their outlines. Photos and cards on the same scan
  stay rectangles.
- **Scans you can find again.** With your own API key (OpenAI, Google AI Studio or DeepSeek), photos are
  turned upright and every photo and PDF gets a short line with the words to search it by —
  `Massachusetts ID card (Mass ID, driver's license)`, `Electric bill, October`. The line goes where your
  tools look: `descript.ion` (Total Commander, Far), the photo's properties (Explorer, Windows Search)
  and the PDF's title. Off until you turn it on; what is sent, and what each service may do with it:
  [Online services](docs/ASSIST.md).
- **Colored paper made white,** like a copier does it: a sheet printed on yellow paper comes out on
  white, with the print and its gray panels kept.
- **It sorts out what lies on the glass.** Several photos laid any old way — tilted, close together,
  with a white sky running into the lid — become separate, straightened files. A sheet of text saved as
  a picture offers to become a PDF, without scanning again.

## Features

- **One window, instant start.** Scanners are found in the background; the window never waits for them.
- **Photos:** found, cropped, straightened and saved one per file. The frames are shown on the preview
  and can be moved, resized, turned, added or removed before scanning.
- **Documents:** margins removed, skew and page orientation (0/90/180/270) corrected — also when only
  reading the page tells which way is up, using the text recognition built into Windows, on your computer.
- **Multi-page PDF:** after each page a card says what comes next ("Put the next page on the glass: it is
  scanned in 15 s"). Rescan or remove any page, insert pages, skip blank ones; the same sheet scanned
  twice is noticed; pages can be added to the last saved PDF later.
- **Day folders and safe names:** `Scans\2026-11-24\scan_001.jpg`, one counter for all formats;
  existing files are never overwritten.
- **Results strip and viewer:** turn, delete or open every saved file right after the scan; a click shows
  it over the whole window, with zoom.
- **Updates by itself:** once a day OmniScan looks for a new version on GitHub and offers to install it;
  it downloads, checks and restarts on its own. Can be turned off.
- **Portable:** settings in a plain `OmniScan.ini` next to the program (or in `%AppData%\OmniScan` if
  that folder is read-only). Nothing is installed, nothing is left behind.
- **29 UI languages**, light and dark theme.

## First start

The executable is not code-signed yet, so Windows SmartScreen may show "Windows protected your PC".
Click **More info** → **Run anyway**. (See [Support](#support) below.)

With scanner sharing turned on, Windows asks once whether to let OmniScan use the network:
allow it for private networks. The server answers only computers on the local network.

## How it works

- Scanning goes through **WIA 2.0**, so any scanner with a Windows driver works.
- Image processing is fully managed code, in memory, built on
  [Leptonica.Managed](https://github.com/sensboston/Leptonica.Managed).
- Sharing uses UDP broadcast for discovery and plain HTTP for scans (raw lossless frames, gzip);
  processing always happens on the PC where you sit.

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

## Support

Shareware and "donateware" belong to another era, and I never quite believed in them anyway, so
OmniScan is free and will stay free and open to everyone.

If you like it, you can donate any amount you don't mind —
[Buy Me a Coffee](https://buymeacoffee.com/sensboston),
[PayPal](https://www.paypal.com/donate/?business=WUS8SDY6CL2XC&no_recurring=0&currency_code=USD) or
[GitHub Sponsors](https://github.com/sponsors/sensboston) (also in the "Sponsor this project" box on the
right of this page). If enough comes in, it will buy an EV code-signing certificate, so that Windows and
antivirus programs stop being suspicious of the download. Paying for one out of my own pocket for a free
program never made sense to me.
