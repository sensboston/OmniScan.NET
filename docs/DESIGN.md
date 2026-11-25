# OmniScan — Design

OmniScan is a small, fast Windows desktop app for scanning documents and photos.
It is a single-window tool that starts instantly, remembers everything, and post-processes
every scan automatically. It ships as one portable executable with settings in an INI file
next to it.

It also shares a USB scanner over the home network: one PC with the scanner runs OmniScan
as a server (tray icon, starts with Windows), and OmniScan on any other PC in the LAN
sees that scanner in its device list and uses it exactly like a local one.

- Platform: Windows 10/11 only, `net10.0-windows`, WPF.
- Assembly name: `OmniScan`, root namespace: `OmniScan.Net`.
- Image processing: fully managed ([Leptonica.Managed](https://github.com/sensboston/Leptonica.Managed)),
  no native imaging DLLs.

---

## 1. Principles

1. **Responsive start.** The main window appears without waiting for scanners, drivers or
   the network. Device enumeration and server discovery run in the background after the
   window is shown; the device list fills in as results arrive.
2. **One screen.** Every control needed for a scan is on the main window. No wizards,
   no start pages, no modal dialogs in the normal flow.
3. **Remember everything.** Device, DPI, color mode, source, output format, root folder,
   processing flags and window placement are restored on the next start (`OmniScan.ini` next to
   the executable, or `%AppData%\OmniScan` when that folder is read-only).
4. **Managed imaging, in memory.** Crop, deskew, rotation, segmentation and encoding run
   in-process on managed code, without temporary files.
5. **Simple and transparent.** No background services besides the optional tray server,
   no telemetry, no auto-updater, no DI containers, no custom protocols where plain HTTP
   does the job, and nothing the user has to configure to make the network part work.

---

## 2. Scanning backends (`IScannerBackend`)

Only current, widely supported Windows interfaces:

| Backend | Purpose |
|---|---|
| **WIA 2.0** | Locally attached (USB) scanners through their WIA driver. Primary backend. |
| **Remote** | A scanner shared by another OmniScan instance in the LAN (see §7). |
| **eSCL (AirScan)** | Planned: network scanners that speak eSCL over HTTP. |

Not supported: TWAIN, SANE, ISIS.

Common capabilities exposed by every backend: flatbed / ADF (where present), DPI,
color mode (Color / Grayscale / Black & White), scan area, preview scan.

---

## 3. Saving

- The user chooses a root folder (default `Pictures\Scans`).
- Each save goes into a per-day subfolder `YYYY-MM-DD`, created on demand.
- File names are `scan_001.png`, `scan_002.jpg`, `scan_003.pdf` … One counter runs through all
  formats (one number is one scan, and sorting by name follows the order of scanning); it continues
  after the highest number in that folder, and existing files are never overwritten. The check and the file
  creation are atomic (`FileMode.CreateNew` with retry), so several photos cut from one
  scan, or a local and a remote scan finishing at once, never collide.
- The date of the folder is taken when the scan (or a multi-page session) starts.
- Output formats: PNG, JPEG, PDF (single or multi-page).

---

## 4. Processing pipeline

Each acquired frame goes through a configurable chain of stages
(`IProcessingStage`, each can be toggled on the main window):

1. **Bed analysis** — the lid surface is modelled and subtracted, so items are found by
   their content, not by the shadow at the glass edge.
2. **Crop margins** — remove the empty glass/lid area around the document or photo.
3. **Separate photos** — if several photos lie on the glass and their outlines separate
   with confidence, each is cut out and saved as its own file. If the confidence
   threshold is not met, the whole sheet is saved as is — a wrong cut is worse than no cut.
   The detected frames are shown on the preview and can be moved, resized, added or removed.
4. **Straighten** — skew correction plus page orientation (0/90/180/270) for text pages.
5. **Turn scans upside down** — a per-scanner option for devices whose frames come out
   rotated by 180 degrees when a page is placed the usual way.
6. **Encode** — PNG / JPEG / PDF. Every result can still be rotated or deleted in the strip
   below the preview.

**Document hint.** When an image format is chosen and a preview or scan is clearly a document
(a sheet with many lines of text, whatever its colors), a bar over the preview offers PDF. Accepting
it continues as a multi-page PDF: after a preview the sheet is scanned as page 1, after a saved scan
that scan becomes page 1 instead of the image file.
Photos standing apart on the glass never count as a document, and an unclear case gets no hint.
The hint is shown at most once per run.

The pipeline always runs on the machine where the user sits (the client), including for
remote scanners: the server only acquires and returns the raw frame (lossless).
This keeps a single place for processing settings and keeps the server trivial.

---

## 5. Multi-page PDF

- Selecting the PDF format starts a session on the first scan.
- After each page a card shows the new page and counts down to the next scan
  (adjustable, 10 s by default): **Scan now** (`Enter`), **Save PDF** (`Ctrl+S`), **Stop** (`Esc`).
- Pages appear in the strip; a marker sets where the next page goes, and any page can be
  rescanned in place, rotated or removed.
- A page that is nearly blank is skipped (the user can keep it).
- The session can also be appended to the last saved PDF.

### Pages turned upright by reading them

Each page is read by the text recognition built into Windows (`Windows.Media.Ocr`, on the computer,
nothing is sent anywhere) as soon as it is added, in the background, to tell which way up it is when
its letter shapes did not (tables, figures, sparse text): text read upside down or sideways comes out
as garbage without the short common words every text has, so the page is read turned over (and
sideways) too, and the clearly better reading wins. The page then turns itself in the strip.

The recognized text is **not** put into the PDF. An invisible text layer was tried and dropped: viewers
such as Chrome recognize the text of a scanned page themselves, on the computer, and better than the
Windows recognizer (minus signs, absolute values, the parts of fractions), but only when the file has
no text of its own.

- **Languages.** A recognizer reads one language, and Windows has those of its installed language packs.
  A language written in Latin letters (German, French) is read by its own recognizer alone. For another
  script (Russian, Greek) the page is read twice, in that language and in English: on a page mostly in
  that script each line is taken from the reading that has mostly such letters, otherwise from the
  English one (a Russian letter with an English address); a page mostly in Latin letters is English, and
  the few look-alike letters of the other reading (Cyrillic "Раде" for "Page") are ignored. Without a
  recognizer for the language, the page is saved without text, as before.
- **PDF writer.** Pages are written by our own small writer, the image compressed as Leptonica does for
  its PDFs (G4 for black and white, JPEG for gray and color). It can also draw words in invisible mode
  with a generated glyphless TrueType font (Unicode through Identity-H and a ToUnicode map); that is
  kept for a better recognizer, unused for now.

---

## 6. Settings dialog

Rarely changed options live in a separate dialog (gear button next to the scanner list):

- UI language: "same as Windows" or any of the bundled translations (Localization/*.json,
  English fallback for missing keys);
- theme: light (default) or dark;
- JPEG quality (50–100, default 75), also used for gray and color images inside PDFs;
- turn scans upside down, per scanner;
- auto-continue delay for multi-page PDF;
- sharing scanners on the network (server role, below);
- the online services tab: API key, turning photos upright, describing photos and PDFs (section 9).

---

## 7. Scanner sharing in the LAN

One executable, two roles that can be active at the same time:

- **Server role** (option in the settings dialog; autostart passes `--server`): a tray icon, no main window.
  Enabling it registers autostart at logon (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
  `OmniScan.exe --server`); disabling removes it. Closing the main window then only hides it.
- **Client role**: the normal main window. Remote scanners appear in the same device list
  as local ones, marked with the host name.

**Single instance per user.** Starting `OmniScan.exe` while it already runs brings the existing
window forward (named mutex + event), so the scanner is never driven by two processes.

**Discovery.** UDP broadcast on port 18524: the client sends a probe, servers answer with
host name, HTTP port and their device list.

**Transport.** Plain HTTP/1.1 on port 18523, served by a small built-in server
(one request per connection, strict limits and timeouts):

| Request | Meaning |
|---|---|
| `GET /api/v1/info` | host name and shared devices |
| `GET /api/v1/capabilities?deviceId=` | resolutions, color modes, sources, bed size |
| `POST /api/v1/scan` | scan parameters (JSON) → raw lossless frame, gzip-compressed |
| `POST /api/v1/rotation` | set the per-scanner "upside down" option on the server |

No authentication: the server answers only clients from the local network.
Windows asks once to allow the app through the firewall.

---

## 8. Solution layout

```text
OmniScan.Net.slnx
├── src/
│   ├── OmniScan.Net.Core/       # settings (INI), naming/folders, IScannerBackend
│   ├── OmniScan.Net.Imaging/    # bed analysis, photo splitting, deskew, orientation, encoding, PDF writer (Leptonica.Managed)
│   ├── OmniScan.Net.Ocr/        # page text with the recognition built into Windows (Windows.Media.Ocr,
│   │                            #   through a C#/WinRT projection of only the types it needs: the whole
│   │                            #   Windows SDK projection would add 25 MB to the program)
│   ├── OmniScan.Net.Assist/     # online services: key → service, model choice, photo turn and captions
│   ├── OmniScan.Net.Scanning/   # WIA backend
│   ├── OmniScan.Net.Remote/     # discovery, HTTP server, remote backend (client side)
│   └── OmniScan.Net.App/        # WPF windows, tray, single instance, translations (AssemblyName: OmniScan)
└── tests/
    └── OmniScan.Net.Tests/      # naming/auto-increment, pipeline on sample scans, protocol round-trip
```

---

## 9. Online services (optional)

With an API key (OpenAI, Google AI Studio, DeepSeek; Anthropic, xAI and OpenRouter untested) photos are
turned upright and photos and PDFs described after saving, in the background; a running bar under a
result shows it is being looked at. The user-facing side is in [ASSIST.md](ASSIST.md).

- **No SDKs:** plain `HttpClient` and JSON; OpenAI-style chat completions for all but Anthropic.
  The service is told by the key's prefix and checked by listing its models; the model is chosen
  from a preferred list per service (`[Assist] Model` in the INI goes first). A busy model (429/5xx)
  gives way to the next one for two minutes, one answering 404 for half an hour.
- **Turn:** the photo is sent as 4 copies (384 px) turned 4 ways, and the service picks the upright
  one (more reliable than asking for an angle); a turn is applied only when a second question with the
  copies in another order agrees. Then the file is written again from the in-memory image.
- **Captions** are asked in the UI language, with words to search by and never numbers, addresses or
  dates of birth. They go to `descript.ion` (UTF-8 with BOM, hidden, as Total Commander makes it), to
  the JPEG's Exif XPSubject/XPComment (read and indexed by Windows), and to a PDF's title.
- **Key:** stored DPAPI-encrypted for the Windows user; an INI copied elsewhere just loses it.

---

## 10. Roadmap

- **v1.0:** everything above except eSCL.
- **v1.1:** the online services (section 9), the built-in viewer, the document hint, photos laid at any angle.
- **Later:** eSCL backend for network scanners.
- **Done:** searchable PDFs, with the text recognition built into Windows (section 5).
