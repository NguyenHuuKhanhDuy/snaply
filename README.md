# Snaply

Local-first screenshot productivity app for Windows, macOS and Linux: **Capture → Annotate → Extract text → Organize → Export.**
Built with C# / .NET 8, Avalonia UI 11 (MVVM), SQLite (EF Core) and SkiaSharp. Nothing is uploaded; no analytics.

## Status (honest summary)

Verified = built and exercised on that OS. Only **Windows 11** has been run so far.

| Area | Windows | macOS | Linux |
|---|---|---|---|
| App shell, navigation, Light/Dark/System themes | Verified | Unverified | Unverified |
| History (SQLite, thumbnails, search, favorites, tags) | Verified (automated tests + manual) | Unverified | Unverified |
| Image editor (crop, rectangle, ellipse, arrow, text, highlight, steps, blur, pixelate, undo/redo, export, copy, save-as-new) | Manual smoke check of capture → editor → arrow; unit tests for model/renderer | Unverified | Unverified |
| Full-screen / region capture, overlay | Verified for single monitor | Implemented, unverified (`screencapture`, needs Screen Recording permission) | Implemented, unverified (maim/scrot/import on X11; grim/gnome-screenshot/spectacle on Wayland) |
| Multi-monitor, mixed DPI | Implemented, **not verified** | Implemented, unverified | X11 via xrandr, unverified |
| Global shortcuts | Implemented (`RegisterHotKey`), not individually verified | **Not implemented** (explained in UI; use `snaply --capture-region` bound in System Settings) | **Not implemented** (X11 planned; Wayland forbids it) |
| Clipboard (image/text) | Implemented | Implemented, unverified (pbcopy/osascript) | Implemented, unverified (xclip / wl-copy) |
| OCR (Tesseract, local) | Engine integrated; recognition needs `eng.traineddata` (see below) | Unverified | Unverified |
| System tray | Not implemented | Not implemented | Not implemented |
| Window capture (P1) | Not implemented | — | — |
| OCR text blocks / selection (P1) | Not implemented | — | — |

Inline editing on the capture overlay (Lightshot style) with configurable auto-save is implemented; delayed capture, repeat last capture, numbered steps, highlight, resize-on-export, favorites, tags, date/format filters, incremental loading and OCR search are implemented.
Region selection is limited to one monitor per drag. Vietnamese OCR is **not claimed**: it is offered only if `vie.traineddata` is installed, and has not been verified.

## Build, run, test

Prerequisites: .NET SDK 8.0 or newer.

```bash
dotnet build Snaply.sln
dotnet run --project src/Snaply.App
dotnet test Snaply.sln
```

Command line: `snaply --capture-region`, `--capture-fullscreen`, `--open` (forwarded to the running instance; use these to bind shortcuts where global hotkeys are unavailable).

## Configuration and data

- Data root: `%LOCALAPPDATA%\Snaply` (Windows), `~/Library/Application Support/Snaply` (macOS), `~/.local/share/Snaply` (Linux). Override with `SNAPLY_DATA_DIR` or `Snaply:DataDirectory` in `appsettings.json`.
- Contains `snaply.db`, `Screenshots/` (default save location, changeable in Settings), `Thumbnails/`, `Logs/`, `Backups/` (pre-migration DB copies), `tessdata/`.
- Settings are stored in the `ApplicationSetting` table with a schema version.

## OCR requirements

Tesseract runs locally. Snaply never downloads language data automatically. Put `<lang>.traineddata` files (for example from the `tessdata_fast` repository) into the `tessdata` folder under the data root (Settings → OCR → Open folder). Only installed languages are listed.

## Platform notes

- **macOS:** grant Screen Recording permission (System Settings → Privacy & Security). Snaply reports `PermissionDenied` instead of returning a blank image.
- **Linux:** install one capture tool (X11: `maim`, `scrot` or ImageMagick `import`; Wayland: `grim`, `gnome-screenshot` or `spectacle`) and `xclip`/`wl-clipboard` for clipboard. The XDG desktop portal is not used yet.
- **Windows:** per-monitor DPI aware via the app manifest.

## Documentation

See [docs/architecture.md](docs/architecture.md), [docs/qa-checklist.md](docs/qa-checklist.md), [docs/packaging.md](docs/packaging.md). Change proposal, design and specs live in `openspec/changes/build-snaply-app/`.
