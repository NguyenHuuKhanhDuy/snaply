# Manual QA checklist (run per OS: Windows, macOS, Linux X11, Linux Wayland)

Record OS/version, display setup (monitors, scaling) and date next to each run.

- [ ] First launch creates the data folder; window shows without errors
- [ ] Theme Light / Dark / System apply everywhere; choice persists after restart; System follows the OS live
- [ ] Capture region: overlay appears, dimensions shown, Esc / right-click cancels, result matches the selection
- [ ] Capture full screen; "all monitors" option; delayed capture countdown; repeat last
- [ ] Missing permission / tool shows a clear message (macOS Screen Recording; Linux without capture tool)
- [ ] Capture with 125% / 150% scaling and with two monitors of different scaling
- [ ] Global shortcuts register; conflict message shown when taken; change and reset in Settings; unsupported explanation where applicable
- [ ] `snaply --capture-region` reaches the running instance
- [ ] Gallery: thumbnails, search, filters, favorite, tags, delete (confirm), missing file state, reveal in folder
- [ ] Editor: every tool, select/move/resize, undo/redo, crop + reset, blur/pixelate in export, copy, export PNG/JPEG, save as new
- [ ] Copy image/text paste correctly into another app
- [ ] OCR with eng data: text shown and copyable; missing language message; cancel
- [ ] Settings: save folder change keeps old screenshots; invalid folder rejected; library check; delete-all confirmation
- [ ] Restart: history, settings and thumbnails persist
- [ ] Keyboard-only navigation and visible focus
