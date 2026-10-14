using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Infrastructure.Storage;

namespace Snaply.App.Services;

/// <summary>User-facing operations on stored screenshots, with notifications. Shared by gallery, details and editor.</summary>
public sealed record BulkResult(int Succeeded, IReadOnlyList<string> Failures);

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


    // ---- bulk operations (gallery multi-select) ----

    /// <summary>Deletes several screenshots after one confirmation. Each failure is reported; the others still go through.</summary>
    public async Task<BulkResult?> DeleteManyAsync(IReadOnlyList<Screenshot> shots)
    {
        if (shots.Count == 0) return null;
        var ok = await _dialogs.ConfirmAsync($"Delete {shots.Count} screenshot{(shots.Count == 1 ? "" : "s")}?",
            "They will be permanently deleted from your library and from disk. This cannot be undone.", "Delete", "Cancel", danger: true).ConfigureAwait(true);
        if (!ok) return null;

        var failures = new List<string>();
        var done = 0;
        foreach (var s in shots)
        {
            var r = await _library.DeleteAsync(s.Id).ConfigureAwait(true);
            if (r.IsSuccess || r.Error!.Code == ErrorCode.NotFound) done++;
            else failures.Add($"{s.FileName}: {r.Error.Message}");
        }
        _events.RaiseChanged();
        Report("deleted", done, failures);
        return new BulkResult(done, failures);
    }

    public async Task<BulkResult> SetFavoriteManyAsync(IReadOnlyList<Screenshot> shots, bool value)
    {
        var failures = new List<string>();
        var done = 0;
        foreach (var s in shots)
        {
            try { await _repo.SetFavoriteAsync(s.Id, value).ConfigureAwait(true); s.IsFavorite = value; done++; }
            catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException) { failures.Add($"{s.FileName}: could not be updated"); }
        }
        _events.RaiseChanged();
        Report(value ? "added to favorites" : "removed from favorites", done, failures);
        return new BulkResult(done, failures);
    }

    public async Task<BulkResult?> TagManyAsync(IReadOnlyList<Screenshot> shots, string? tagName)
    {
        var name = tagName?.Trim();
        if (shots.Count == 0 || string.IsNullOrEmpty(name)) return null;
        if (name.Length > 50) { _notes.Show(NotificationKind.Warning, "Tag is too long", "Tags can be at most 50 characters."); return null; }

        var tag = await _repo.GetOrCreateTagAsync(name).ConfigureAwait(true);
        var failures = new List<string>();
        var done = 0;
        foreach (var s in shots)
        {
            try { await _repo.AddTagAsync(s.Id, tag.Id).ConfigureAwait(true); done++; }
            catch (Exception ex) when (ex is Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException) { failures.Add($"{s.FileName}: could not be tagged"); }
        }
        _events.RaiseChanged();
        Report($"tagged “{name}”", done, failures);
        return new BulkResult(done, failures);
    }

    /// <summary>Copies the original files to a chosen folder, never overwriting: name clashes get a numeric suffix.</summary>
    public async Task<BulkResult?> ExportManyAsync(IReadOnlyList<Screenshot> shots)
    {
        if (shots.Count == 0) return null;
        var folder = await _dialogs.PickFolderAsync("Choose where to export").ConfigureAwait(true);
        if (folder is null) return null;

        var failures = new List<string>();
        var done = 0;
        foreach (var s in shots)
        {
            try
            {
                if (!File.Exists(s.FilePath)) { failures.Add($"{s.FileName}: the file is missing"); continue; }
                var bytes = await File.ReadAllBytesAsync(s.FilePath).ConfigureAwait(true);
                var r = await _storage.ExportAsync(bytes, UniquePath(folder, s.FileName), overwrite: false).ConfigureAwait(true);
                if (r.IsSuccess) done++; else failures.Add($"{s.FileName}: {r.Error!.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add($"{s.FileName}: could not be read"); }
        }
        Report("exported", done, failures);
        return new BulkResult(done, failures);
    }

    public static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName); var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(folder, $"{stem}-{i}{ext}");
            if (!File.Exists(path)) return path;
        }
    }

    private void Report(string verb, int done, IReadOnlyList<string> failures)
    {
        if (failures.Count == 0) { _notes.Show(NotificationKind.Success, $"{done} screenshot{(done == 1 ? "" : "s")} {verb}"); return; }
        var detail = string.Join("\n", failures.Take(3)) + (failures.Count > 3 ? $"\n…and {failures.Count - 3} more" : "");
        _notes.Show(NotificationKind.Warning, $"{done} {verb}, {failures.Count} failed", detail);
    }

    public void MissingFile() =>
        _notes.Show(NotificationKind.Warning, "File not found", "The screenshot file was moved or deleted. You can remove the entry from the library.");
}
