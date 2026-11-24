**What's new**

- JPEG quality setting (50–100, default 90). It also applies to photos and gray pages inside PDFs, which used a fixed quality of 75 before.
- The theme is light or dark; the "same as Windows" choice is gone (it falls back to light).

**Downloads**

Two downloads below — the same program, pick one:

| File | Size | Needs |
|---|---|---|
| **`OmniScan.exe`** | ~68 MB | nothing — the .NET runtime is inside. If unsure, take this one. |
| **`OmniScan-lite.exe`** | ~9 MB | the [.NET 10 Desktop Runtime (x64)](https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe) installed once. Starts a bit faster. |

Both are single portable files: nothing to install, put the file in any folder and run it.
Settings are kept in `OmniScan.ini` next to it. If the runtime is missing, `OmniScan-lite.exe`
says so on start and offers the download page; installing the runtime once is enough for all
.NET 10 programs, and Windows Update keeps it patched.

The executables are not code-signed yet, so on first start Windows SmartScreen may say
"Windows protected your PC": click **More info** → **Run anyway**.

Requires Windows 10 or 11 (64-bit).
