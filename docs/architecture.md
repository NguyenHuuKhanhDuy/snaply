# Architecture

```
Snaply.App            Avalonia views, view models, theming, overlay, DI composition
Snaply.Core           Models, interfaces, Result/Error types, capture orchestrator, hotkey manager, OCR workflow, settings validation (no UI/OS code)
Snaply.Infrastructure EF Core + SQLite, migrations, settings service, file storage, thumbnails, screenshot library
Snaply.Editor         Non-destructive document model, commands (undo/redo), versioned serialization, Skia renderer
Snaply.Ocr            Tesseract implementation of IOcrService
Snaply.Platform       Windows / macOS / Linux adapters (capture, clipboard, hotkeys, shell) selected at runtime
```

Capture and history orchestration live in Core/Infrastructure rather than separate projects to avoid near-empty assemblies.

Key decisions
- Region capture grabs the virtual desktop once, shows frozen-frame overlays per monitor, then crops (the overlay never appears in the image). Frame/area mapping is scale-aware (`CaptureOrchestrator.ToFramePixels`), covering Retina-style frames.
- Failures are typed (`Result`, `ErrorCode`); adapters never return blank images.
- The editor renders with a single `DocumentRenderer` for preview and export. Annotations are immutable records changed through commands; the original file is never modified. Documents serialize through `EditorDocumentDtoV1`; newer/corrupt versions are reported, not thrown.
- Saving: write temp file → atomic move → thumbnail → DB row; on failure created files are removed. Deleting removes the row first and reports files that could not be deleted; `VerifyAsync` later finds orphans.
- DB: EF Core migrations applied at startup with a backup before upgrades; a database from a newer build is refused.
- Search uses escaped `LIKE` (filename and OCR text); gallery paging is keyset-based on `(CreatedAt, Id)`.

## Database
`Screenshots` (unique `FilePath`, indexes on `(CreatedAt, Id)`, `IsFavorite`, `FileName`), `AnnotationDocuments` (unique `ScreenshotId`, cascade), `Tags` (unique, case-insensitive `Name`), `ScreenshotTags` (composite key, cascade), `ApplicationSettings` (key/value).

## Deviations from the original design
- Linux: XDG portal capture and X11 global hotkeys are not implemented; macOS global hotkeys are not implemented.
- "Save as new" stores the flattened result as an independent library entry; edits stay attached to the original.
