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
- File names are `scan_001.png`, `scan_002.jpg`, `scan_001.pdf` … The counter is the next
  free number in that folder; existing files are never overwritten. The check and the file
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
(a sheet with many lines of text, whatever its colors), a hint in the status line offers to switch
to PDF; after a saved scan it redoes that scan as the first page of a PDF instead of the image.
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

---

## 6. Settings dialog

Rarely changed options live in a separate dialog (gear button next to the scanner list):

- UI language: "same as Windows" or any of the bundled translations (Localization/*.json,
  English fallback for missing keys);
- theme: light (default) or dark;
- JPEG quality (50–100, default 75), also used for gray and color images inside PDFs;
- turn scans upside down, per scanner;
- auto-continue delay for multi-page PDF;
- sharing scanners on the network (server role, below).

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
│   ├── OmniScan.Net.Imaging/    # bed analysis, photo splitting, deskew, orientation, encoding (Leptonica.Managed)
│   ├── OmniScan.Net.Scanning/   # WIA backend
│   ├── OmniScan.Net.Remote/     # discovery, HTTP server, remote backend (client side)
│   └── OmniScan.Net.App/        # WPF windows, tray, single instance, translations (AssemblyName: OmniScan)
└── tests/
    └── OmniScan.Net.Tests/      # naming/auto-increment, pipeline on sample scans, protocol round-trip
```

---

## 9. Roadmap

- **v1.0:** everything above except eSCL.
- **Later:** eSCL backend for network scanners; photo orientation.
- **v2.0:** OCR with a fully managed engine and an invisible, selectable text layer in PDFs
  (searchable PDF) on top of the processed page image.
