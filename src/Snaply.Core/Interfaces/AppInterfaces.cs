using Snaply.Core.Common;
using Snaply.Core.Models;

namespace Snaply.Core.Interfaces;

public interface IApplicationSettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Validates then applies <paramref name="mutate"/> on a copy; persists and raises <see cref="Changed"/> only if valid.</summary>
    Task<Result> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default);

    string EffectiveSaveDirectory { get; }
}

public enum NotificationKind { Info, Success, Warning, Error }

public interface INotificationService
{
    void Show(NotificationKind kind, string title, string? message = null);
}

/// <summary>Lets the capture workflow hide/show the app window and bring it to front.</summary>
public interface IAppWindowController
{
    Task HideAsync();
    Task RestoreAsync();
    void OpenScreenshotInEditor(long screenshotId);
}

public enum SelectionAction { Default, Save, Copy }

/// <param name="EditInline">Show the in-place annotation toolbar after the area is chosen.</param>
/// <param name="Preselect">Start with this virtual-desktop area already selected (used for full-screen captures).</param>
public sealed record RegionSelectOptions(bool EditInline, bool AutoSave, bool CopyEnabled, PixelRect? Preselect = null);

/// <param name="EditedImage">The cropped image with inline annotations applied; null when no inline editing took place.</param>
public sealed record RegionSelection(PixelRect Area, RawImage? EditedImage, SelectionAction Action);

/// <summary>Shows the frozen-frame overlay and returns the user choice, or null if cancelled.</summary>
public interface IRegionSelector
{
    Task<RegionSelection?> SelectAsync(RawImage frozenFrame, PixelRect virtualBounds, IReadOnlyList<MonitorInfo> monitors, RegionSelectOptions options, CancellationToken ct = default);
}

public sealed record ScreenshotQuery(
    string? Search = null,
    bool FavoritesOnly = false,
    long? TagId = null,
    string? Format = null,
    DateTime? From = null,
    DateTime? To = null);

public readonly record struct PageCursor(DateTime CreatedAt, long Id);

public interface IScreenshotRepository
{
    Task<Screenshot> AddAsync(Screenshot screenshot, CancellationToken ct = default);
    Task<Screenshot?> GetAsync(long id, CancellationToken ct = default);
    Task<Screenshot?> GetByPathAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<Screenshot>> QueryAsync(ScreenshotQuery query, PageCursor? after, int take, CancellationToken ct = default);
    Task<int> CountAsync(ScreenshotQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<Screenshot>> ListAllAsync(CancellationToken ct = default);
    Task UpdateFileInfoAsync(long id, string? thumbnailPath, CancellationToken ct = default);
    Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken ct = default);
    Task SetOcrAsync(long id, string? text, string? language, CancellationToken ct = default);
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken ct = default);
    Task<Tag> GetOrCreateTagAsync(string name, CancellationToken ct = default);
    Task AddTagAsync(long screenshotId, long tagId, CancellationToken ct = default);
    Task RemoveTagAsync(long screenshotId, long tagId, CancellationToken ct = default);
    Task DeleteTagAsync(long tagId, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> GetTagsForAsync(long screenshotId, CancellationToken ct = default);

    Task<AnnotationDocument?> GetAnnotationDocumentAsync(long screenshotId, CancellationToken ct = default);
    Task SaveAnnotationDocumentAsync(long screenshotId, int version, string data, CancellationToken ct = default);
}

/// <summary>High-level library operations that keep database rows, image files and thumbnails consistent.</summary>
public interface IScreenshotLibrary
{
    Task<Result<Screenshot>> AddAsync(RawImage image, CaptureMethod method, CancellationToken ct = default);

    /// <summary>Adds an already-encoded image (e.g. editor output). Extension decides the format.</summary>
    Task<Result<Screenshot>> AddEncodedAsync(byte[] data, string extension, int width, int height, CaptureMethod method, CancellationToken ct = default);

    /// <summary>Removes DB row, thumbnail and image file. Reports file deletion failures instead of hiding them.</summary>
    Task<Result> DeleteAsync(long id, CancellationToken ct = default);

    Task<Result> EnsureThumbnailAsync(Screenshot screenshot, CancellationToken ct = default);
    Task<LibraryVerification> VerifyAsync(CancellationToken ct = default);
    Task<int> DeleteOrphansAsync(LibraryVerification verification, CancellationToken ct = default);
}

public sealed record LibraryVerification(
    IReadOnlyList<long> MissingFileScreenshotIds,
    IReadOnlyList<string> OrphanImageFiles,
    IReadOnlyList<string> OrphanThumbnails);

public sealed record OcrBlock(string Text, int X, int Y, int Width, int Height, float Confidence);

public sealed record OcrResult(string Text, IReadOnlyList<OcrBlock> Blocks, string Language, string Engine);

public interface IOcrService
{
    string EngineName { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<string> GetInstalledLanguages();
    Task<Result<OcrResult>> RecognizeAsync(string imagePath, IReadOnlyList<string> languages, CancellationToken ct = default);
}
