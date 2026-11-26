**What's new in 1.1.0**

- **Photos turned upright and described (optional, with your own API key).** Enter a key of OpenAI,
  Google AI Studio or DeepSeek in **Settings → AI**, and OmniScan has it turn photos the right way up
  after saving, and write a short line for every photo and PDF with words to find it by
  (`Massachusetts ID card (Mass ID, driver's license)`): into `descript.ion` (Total Commander, Far),
  the photo's properties (Explorer, Windows Search) and the PDF's title. What is sent, and what each
  service may do with it: [Online services](https://sensboston.github.io/OmniScan.NET/ASSIST.html).
  Off by default; nothing leaves your computer without a key.
- **Stickers**: with PNG, the new **Stickers** checkbox cuts each picture along its outline with a
  white border, on a transparent background (photos and cards stay rectangles). When several stickers
  lie on the glass, OmniScan offers it by itself, and the preview shows their outlines.
- **Colored paper**: the new **Remove paper color** checkbox gives sheets and PDF pages printed on
  colored paper a white background (photos keep their colors).
- **Updates**: OmniScan looks for a new version on GitHub once a day and offers to install it
  (Yes/No); it downloads, checks and restarts by itself. Can be turned off in **Settings → General**.
  This works from 1.1.0 on: from 1.0.x, download 1.1.0 by hand once.
- **A shared scanner in use**: while someone scans at the computer the scanner is attached to, a scan
  from another computer is refused at once instead of taking what lies on the glass; the scanner list
  shows a green or red dot, and the other computer can ask for the scanner.
- **"Looks like a document"**: when a JPEG or PNG scan is a sheet of text, a card offers to make it a
  PDF instead, right away and without scanning again.
- **Built-in viewer**: click a result to see it over the whole window (wheel zooms, arrows go through
  the results); a double-click opens it in your usual program.
- **Photos laid any old way** at steep angles, close to each other, are cut out one by one (before,
  two tilted prints could become one "document"). A white sky or table running into the lid no
  longer cuts a strip off a photo, and the thin light edge of the paper is trimmed.
- **Sparse pages** (a test with room for the answers) stay one page instead of being cut into pieces.
- **PDF pages turned upright** also when only reading them tells which way is up (tables, figures);
  done on your computer by the text recognition built into Windows.
- **PDF**: the card after each page says what to do ("Put the next page on the glass: it is scanned in
  15 s"), and the countdown is 15 seconds by default (was 10).
- **One counter for all formats**: `scan_001.png`, `scan_002.jpg`, `scan_003.pdf`.
- **Settings in tabs**, and the scanner's "top of the page" chosen from two pictures.
- The settings panel is locked while a scan is running; an older copy of OmniScan no longer wipes out
  settings it does not know in a shared `OmniScan.ini`.

**Downloads**

Two downloads below — the same program, pick one:

| File | Size | Needs |
|---|---|---|
| **`OmniScan.exe`** | ~68 MB | nothing — the .NET runtime is inside. If unsure, take this one. |
| **`OmniScan-lite.exe`** | ~10 MB | the [.NET 10 Desktop Runtime (x64)](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe) installed once. Starts a bit faster. |

Both are single portable files: nothing to install, put the file in any folder and run it.
Settings are kept in `OmniScan.ini` next to it. If the runtime is missing, `OmniScan-lite.exe`
says so on start and offers the download page; installing the runtime once is enough for all
.NET 10 programs, and Windows Update keeps it patched.

The executables are not code-signed yet, so on first start Windows SmartScreen may say
"Windows protected your PC": click **More info** → **Run anyway**.

Requires Windows 10 (version 1809 or later) or Windows 11, 64-bit.

OmniScan is free and stays free. If you like it, see [Support](https://github.com/sensboston/OmniScan.NET#support) — donations go to a code-signing certificate.
