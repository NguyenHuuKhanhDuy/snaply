# Packaging (initial strategy, not production-ready)

Self-contained publish per runtime:

```bash
dotnet publish src/Snaply.App -c Release -r win-x64 --self-contained -o artifacts/win-x64
dotnet publish src/Snaply.App -c Release -r osx-arm64 --self-contained -o artifacts/osx-arm64
dotnet publish src/Snaply.App -c Release -r linux-x64 --self-contained -o artifacts/linux-x64
```

Planned installers (none produced or tested yet): Windows MSIX or Inno Setup; macOS `.app` bundle with signing and notarization (Screen Recording usage must be user-granted); Linux AppImage.
Bundle `eng.traineddata` in a `tessdata/` folder next to the executable if offline OCR out of the box is wanted.
