using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Infrastructure.Storage;

namespace Snaply.App.Services;

/// <summary>User-facing operations on stored screenshots, with notifications. Shared by gallery, details and editor.</summary>
public sealed class ScreenshotActions
{
    private readonly IScreenshotLibrary _library;
    private readonly IScreenshotRepository _repo;
    private readonly IClipboardService _clipboard;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly INotificationService _notes;
    private readonly ImageStorageService _storage;
    private readonly LibraryEvents _events;
    private readonly ILogger<ScreenshotActions> _log;

    public ScreenshotActions(IScreenshotLibrary library, IScreenshotRepository repo, IClipboardService clipboard, IShellService shell,
        DialogService dialogs, INotificationService notes, ImageStorageService storage, LibraryEvents events, ILogger<ScreenshotActions> log)
    {
        _library = library; _repo = repo; _clipboard = clipboard; _shell = shell; _dialogs = dialogs; _notes = notes;
        _storage = storage; _events = events; _log = log;
    }

    public bool CanReveal => _shell.CanRevealInFolder;

    public async Task CopyImageAsync(Screenshot shot)
    {
        if (!File.Exists(shot.FilePath)) { MissingFile(); return; }
        var raw = await Task.Run(() =>
        {
            using var bmp = ImageCodec.DecodeSafe(shot.FilePath);
            return bmp is null ? null : ImageBridge.ToRaw(bmp);
        }).ConfigureAwait(true);
        if (raw is null) { _notes.Show(NotificationKind.Error, "Could not read the image", "The file may be damaged."); return; }

        var r = await _clipboard.SetImageAsync(raw).ConfigureAwait(true);
        if (r.IsSuccess) _notes.Show(NotificationKind.Success, "Image copied to clipboard");
        else _notes.Show(NotificationKind.Error, "Could not copy the image", r.Error!.Message);
    }

    public async Task CopyTextAsync(string text, string successTitle = "Text copied to clipboard")
    {
        var r = await _clipboard.SetTextAsync(text).ConfigureAwait(true);
        if (r.IsSuccess) _notes.Show(NotificationKind.Success, successTitle);
        else _notes.Show(NotificationKind.Error, "Could not copy the text", r.Error!.Message);
    }

    public async Task ExportAsync(Screenshot shot)
    {
        if (!File.Exists(shot.FilePath)) { MissingFile(); return; }
        var ext = shot.Format == "jpg" ? "jpg" : "png";
        var path = await _dialogs.PickSaveFileAsync(Path.GetFileNameWithoutExtension(shot.FileName) + "-export", ext, ext.ToUpperInvariant() + " image").ConfigureAwait(true);
        if (path is null) return;

        try
        {
            var wanted = ImageCodec.FormatFromExtension(Path.GetExtension(path)) ?? ImageCodec.FormatFromExtension(ext)!.Value;
            var bytes = await Task.Run(() =>
            {
                if (ImageCodec.Extension(wanted) == shot.Format) return File.ReadAllBytes(shot.FilePath);
                using var bmp = ImageCodec.DecodeSafe(shot.FilePath);
                return bmp is null ? null : ImageCodec.Encode(bmp, wanted);
            }).ConfigureAwait(true);
            if (bytes is null) { _notes.Show(NotificationKind.Error, "Could not read the image"); return; }

            var r = await _storage.ExportAsync(bytes, path, overwrite: true).ConfigureAwait(true); // the picker already confirmed overwrite
            if (r.IsSuccess) _notes.Show(NotificationKind.Success, "Image exported", Path.GetFileName(path));
            else _notes.Show(NotificationKind.Error, "Export failed", r.Error!.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Export failed");
            _notes.Show(NotificationKind.Error, "Export failed", "The source file could not be read.");
        }
    }

    public void Reveal(Screenshot shot)
    {
        var r = _shell.RevealInFolder(shot.FilePath);
        if (!r.IsSuccess) _notes.Show(NotificationKind.Warning, "Could not open the folder", r.Error!.Message);
    }

    public async Task<bool> DeleteAsync(Screenshot shot)
    {
        var ok = await _dialogs.ConfirmAsync("Delete screenshot?",
            $"'{shot.FileName}' will be permanently deleted from your library and from disk. This cannot be undone.",
            "Delete", "Cancel", danger: true).ConfigureAwait(true);
        if (!ok) return false;

        var r = await _library.DeleteAsync(shot.Id).ConfigureAwait(true);
        _events.RaiseChanged();
        if (r.IsSuccess) _notes.Show(NotificationKind.Success, "Screenshot deleted");
        else _notes.Show(r.Error!.Code == ErrorCode.NotFound ? NotificationKind.Info : NotificationKind.Warning, "Screenshot removed", r.Error.Message);
        return true;
    }

    public async Task SetFavoriteAsync(Screenshot shot, bool value)
    {
        await _repo.SetFavoriteAsync(shot.Id, value).ConfigureAwait(true);
        shot.IsFavorite = value;
        _events.RaiseChanged();
    }

    public void MissingFile() =>
        _notes.Show(NotificationKind.Warning, "File not found", "The screenshot file was moved or deleted. You can remove the entry from the library.");
}
