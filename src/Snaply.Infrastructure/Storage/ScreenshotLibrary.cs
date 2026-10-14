using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Infrastructure.Storage;

/// <summary>Keeps database rows, image files and thumbnails consistent across add, delete and verify operations.</summary>
public sealed class ScreenshotLibrary : IScreenshotLibrary
{
    private readonly IScreenshotRepository _repo;
    private readonly ImageStorageService _storage;
    private readonly ThumbnailService _thumbs;
    private readonly IApplicationSettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ILogger<ScreenshotLibrary> _log;

    public ScreenshotLibrary(IScreenshotRepository repo, ImageStorageService storage, ThumbnailService thumbs,
        IApplicationSettingsService settings, AppPaths paths, ILogger<ScreenshotLibrary> log)
    {
        _repo = repo; _storage = storage; _thumbs = thumbs; _settings = settings; _paths = paths; _log = log;
    }

    public async Task<Result<Screenshot>> AddAsync(RawImage image, CaptureMethod method, CancellationToken ct = default)
    {
        byte[] data;
        try { data = ImageCodec.Encode(image, _settings.Current.DefaultFormat); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _log.LogError(ex, "Encoding failed");
            return Result<Screenshot>.Fail(ErrorCode.Failed, "The image could not be encoded.");
        }
        return await AddEncodedAsync(data, ImageCodec.Extension(_settings.Current.DefaultFormat), image.Width, image.Height, method, ct).ConfigureAwait(false);
    }

    public async Task<Result<Screenshot>> AddEncodedAsync(byte[] data, string extension, int width, int height, CaptureMethod method, CancellationToken ct = default)
    {
        var format = ImageCodec.FormatFromExtension(extension);
        if (format is null) return Result<Screenshot>.Fail(ErrorCode.InvalidInput, "Unsupported image format.");

        var saved = await _storage.SaveAsync(data, extension, _settings.EffectiveSaveDirectory, ct).ConfigureAwait(false);
        if (!saved.IsSuccess) return Result<Screenshot>.Fail(saved.Error!);

        string? thumbPath = null;
        try
        {
            var thumb = await _thumbs.GenerateAsync(saved.Value.Path, ct).ConfigureAwait(false);
            if (thumb.IsSuccess) thumbPath = thumb.Value;
            else _log.LogWarning("Thumbnail generation failed for new screenshot: {Error}", thumb.Error);

            var shot = new Screenshot
            {
                FilePath = saved.Value.Path,
                FileName = saved.Value.FileName,
                ThumbnailPath = thumbPath,
                CreatedAt = DateTime.UtcNow,
                Width = width,
                Height = height,
                Format = ImageCodec.Extension(format.Value),
                FileSize = saved.Value.Size,
                CreatedByCaptureMethod = method,
            };
            var added = await _repo.AddAsync(shot, ct).ConfigureAwait(false);
            return Result<Screenshot>.Ok(added);
        }
        catch (OperationCanceledException)
        {
            Cleanup(saved.Value.Path, thumbPath);
            throw;
        }
        catch (Exception ex)
        {
            // Database failure after the file was written: remove what we created so no orphans remain.
            _log.LogError(ex, "Saving screenshot metadata failed; cleaning up files");
            Cleanup(saved.Value.Path, thumbPath);
            return Result<Screenshot>.Fail(ErrorCode.Failed, "The screenshot could not be added to the library.");
        }
    }

    private static void Cleanup(string imagePath, string? thumbPath)
    {
        ImageStorageService.TryDelete(imagePath);
        if (thumbPath is not null) ImageStorageService.TryDelete(thumbPath);
    }

    public async Task<Result> DeleteAsync(long id, CancellationToken ct = default)
    {
        var shot = await _repo.GetAsync(id, ct).ConfigureAwait(false);
        if (shot is null) return Result.Fail(ErrorCode.NotFound, "The screenshot no longer exists.");

        await _repo.DeleteAsync(id, ct).ConfigureAwait(false);

        var failed = new List<string>();
        if (!ImageStorageService.TryDelete(shot.FilePath)) failed.Add(shot.FileName);
        if (shot.ThumbnailPath is not null) ImageStorageService.TryDelete(shot.ThumbnailPath);

        if (failed.Count == 0) return Result.Ok();
        _log.LogWarning("Screenshot {Id} removed from library but its file could not be deleted", id);
        return Result.Fail(ErrorCode.StorageUnavailable,
            $"Removed from the library, but the file '{shot.FileName}' could not be deleted. It may be open in another program; library verification can clean it up later.");
    }

    public async Task<Result> EnsureThumbnailAsync(Screenshot screenshot, CancellationToken ct = default)
    {
        if (screenshot.ThumbnailPath is not null && File.Exists(screenshot.ThumbnailPath)) return Result.Ok();
        if (!File.Exists(screenshot.FilePath)) return Result.Fail(ErrorCode.NotFound, "The screenshot file cannot be found.");

        var thumb = await _thumbs.GenerateAsync(screenshot.FilePath, ct).ConfigureAwait(false);
        if (!thumb.IsSuccess) return Result.Fail(thumb.Error!);

        screenshot.ThumbnailPath = thumb.Value;
        await _repo.UpdateFileInfoAsync(screenshot.Id, thumb.Value, ct).ConfigureAwait(false);
        return Result.Ok();
    }

    public async Task<LibraryVerification> VerifyAsync(CancellationToken ct = default)
    {
        var all = await _repo.ListAllAsync(ct).ConfigureAwait(false);
        var missing = all.Where(s => !File.Exists(s.FilePath)).Select(s => s.Id).ToList();

        var known = new HashSet<string>(all.Select(s => s.FilePath), StringComparer.OrdinalIgnoreCase);
        var orphanImages = new List<string>();
        var dir = _settings.EffectiveSaveDirectory;
        if (Directory.Exists(dir))
            orphanImages.AddRange(Directory.EnumerateFiles(dir, ImageStorageService.FilePrefix + "*")
                .Where(f => !known.Contains(f)));

        var knownThumbs = new HashSet<string>(all.Where(s => s.ThumbnailPath != null).Select(s => s.ThumbnailPath!), StringComparer.OrdinalIgnoreCase);
        var orphanThumbs = Directory.Exists(_paths.ThumbnailsDirectory)
            ? Directory.EnumerateFiles(_paths.ThumbnailsDirectory).Where(f => !knownThumbs.Contains(f)).ToList()
            : new List<string>();

        return new LibraryVerification(missing, orphanImages, orphanThumbs);
    }

    public Task<int> DeleteOrphansAsync(LibraryVerification verification, CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var f in verification.OrphanImageFiles.Concat(verification.OrphanThumbnails))
            if (ImageStorageService.TryDelete(f)) deleted++;
        return Task.FromResult(deleted);
    }
}
