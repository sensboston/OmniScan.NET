**What's new in 1.1.0**

- **Photos turned upright and described (optional, with your own API key).** Enter a key of OpenAI,
  Google AI Studio or DeepSeek in **Settings → AI**, and OmniScan has it turn photos the right way up
  after saving, and write a short line for every photo and PDF with words to find it by
  (`Massachusetts ID card (Mass ID, driver's license)`): into `descript.ion` (Total Commander, Far),
  the photo's properties (Explorer, Windows Search) and the PDF's title. What is sent, and what each
  service may do with it: [Online services](https://sensboston.github.io/OmniScan.NET/ASSIST.html).
  Off by default; nothing leaves your computer without a key.
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
- **One counter for all formats**: `scan_001.png`, `scan_002.jpg`, `scan_003.pdf`.
- **Settings in tabs**, and the scanner's "top of the page" chosen from two pictures.
- The settings panel is locked while a scan is running; an older copy of OmniScan no longer wipes out
  settings it does not know in a shared `OmniScan.ini`.

**Downloads**

Two downloads below — the same program, pick one:

| File | Size | Needs |
|---|---|---|
| **`OmniScan.exe`** | ~74 MB | nothing — the .NET runtime is inside. If unsure, take this one. |
| **`OmniScan-lite.exe`** | ~35 MB | the [.NET 10 Desktop Runtime (x64)](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe) installed once. Starts a bit faster. |

Both are single portable files: nothing to install, put the file in any folder and run it.
Settings are kept in `OmniScan.ini` next to it. If the runtime is missing, `OmniScan-lite.exe`
says so on start and offers the download page; installing the runtime once is enough for all
.NET 10 programs, and Windows Update keeps it patched.

The executables are not code-signed yet, so on first start Windows SmartScreen may say
"Windows protected your PC": click **More info** → **Run anyway**.

Requires Windows 10 (version 1809 or later) or Windows 11, 64-bit.
