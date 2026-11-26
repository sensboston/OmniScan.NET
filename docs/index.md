---
layout: default
---

**English** · [Русский](index.ru.html) · [Українська](index.uk.html)

## Scan documents and photos — quickly, and without the clutter

OmniScan is a small Windows app for flatbed scanners. It opens in a moment, shows one window
with everything you need, and tidies every scan by itself: crops the margins, straightens the page,
cuts several photos lying on the glass into separate files — and even cuts stickers out along their outline.

[**Download OmniScan.exe**](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan.exe)
&nbsp;·&nbsp; Windows 10 (1809+) or 11, 64-bit &nbsp;·&nbsp; free and open source (MIT)

One portable file: nothing to install, no runtime to download. Put it in any folder and run it.

Already have the [.NET 10 Desktop Runtime](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe) installed? Take the
[lite build](https://github.com/sensboston/OmniScan.NET/releases/latest/download/OmniScan-lite.exe)
instead: the same program, about 10 MB instead of 68 MB.

<p align="center">
  <a href="https://github.com/user-attachments/assets/7db07fd7-f8ed-47ca-8ff1-8ae0528eee87"><img src="https://github.com/user-attachments/assets/7db07fd7-f8ed-47ca-8ff1-8ae0528eee87" width="45%" alt="Photos found on the glass, cut out and saved"></a>
  <a href="https://github.com/user-attachments/assets/23c3d83b-6f6c-4e53-a8c1-f11ee70fd6bd"><img src="https://github.com/user-attachments/assets/23c3d83b-6f6c-4e53-a8c1-f11ee70fd6bd" width="45%" alt="Stickers outlined on the preview"></a>
  <a href="https://github.com/user-attachments/assets/1450b850-7575-490f-9924-60aed87455c1"><img src="https://github.com/user-attachments/assets/1450b850-7575-490f-9924-60aed87455c1" width="45%" alt="A scan that looks like a document offers PDF"></a>
  <a href="https://github.com/user-attachments/assets/242e2d19-7d2a-4ca6-94b5-b199fa467894"><img src="https://github.com/user-attachments/assets/242e2d19-7d2a-4ca6-94b5-b199fa467894" width="45%" alt="A PDF page, with its description for search"></a>
</p>

### What it does

- **Photos:** several photos on the glass become several neatly cropped, straightened files — also when
  they are put down any old way, tilted and close together. The detected frames are shown on the
  preview, and you can adjust them before saving.
- **Stickers:** cut out along their outline with a clean white border, on a transparent background
  (PNG). OmniScan notices stickers by itself and offers this; the preview shows their outlines.
- **Documents:** margins removed, skew and page orientation corrected. A picture scan that looks like a
  document offers to become a PDF, without scanning again.
- **Colored paper made white,** like a copier does it: a page printed on yellow paper comes out on white.
- **Multi-page PDF:** scan page after page; a card after each page says what to do next. Reorder,
  rescan or remove pages, skip blank ones, or add pages to the last PDF.
- **Tidy folders:** every scan goes to a folder for the day (`Scans\2026-11-24\scan_001.jpg`),
  and nothing is ever overwritten. A click on a result shows it over the whole window.
- **Find your scans later (optional):** with an API key of OpenAI, Google AI Studio or DeepSeek, photos
  are turned upright and every photo and PDF gets a short description with words to search it by —
  "Mass ID", "electric bill". [What is sent, and to whom](ASSIST.html).
- **Share a scanner:** the scanner attached to one PC can be used from every other PC at home.
  Turn on sharing on that PC, and OmniScan on the others lists the scanner as if it were their own;
  while someone is scanning there, the others see it and can ask for the scanner.
- **Keeps itself up to date:** a new version is offered once it is out, and installed with one click.
- **29 languages**, light and dark theme; everything you choose is remembered.

### First start

The program is not code-signed yet, so Windows SmartScreen may show
"Windows protected your PC". Click **More info** → **Run anyway**. When you turn on scanner
sharing, Windows also asks once whether to let OmniScan use the network — allow it for private networks.

### Free, and staying free

Shareware and "donateware" belong to another era, so OmniScan is free and will stay free and open to
everyone. If you like it, you can donate any amount you don't mind —
[Buy Me a Coffee](https://buymeacoffee.com/sensboston),
[PayPal](https://www.paypal.com/donate/?business=WUS8SDY6CL2XC&no_recurring=0&currency_code=USD) or
[GitHub Sponsors](https://github.com/sponsors/sensboston). If enough comes in, it will buy a code-signing
certificate, so that Windows and antivirus programs stop being suspicious of the download.

### Source code

[github.com/sensboston/OmniScan.NET](https://github.com/sensboston/OmniScan.NET)
