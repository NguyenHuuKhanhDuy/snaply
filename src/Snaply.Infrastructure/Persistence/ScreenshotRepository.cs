using Microsoft.EntityFrameworkCore;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Infrastructure.Persistence;

public sealed class ScreenshotRepository : IScreenshotRepository
{
    private readonly IDbContextFactory<SnaplyDbContext> _factory;

    public ScreenshotRepository(IDbContextFactory<SnaplyDbContext> factory) => _factory = factory;

    public async Task<Screenshot> AddAsync(Screenshot screenshot, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // Idempotent per file path: a retried save must not create a second history entry.
        var existing = await db.Screenshots.FirstOrDefaultAsync(x => x.FilePath == screenshot.FilePath, ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        db.Screenshots.Add(screenshot);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return screenshot;
    }

    public async Task<Screenshot?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Screenshots.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
    }

    public async Task<Screenshot?> GetByPathAsync(string filePath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Screenshots.AsNoTracking().FirstOrDefaultAsync(x => x.FilePath == filePath, ct).ConfigureAwait(false);
    }

    private static IQueryable<Screenshot> Filter(IQueryable<Screenshot> q, ScreenshotQuery f)
    {
        if (f.FavoritesOnly) q = q.Where(x => x.IsFavorite);
        if (f.TagId is { } tag) q = q.Where(x => x.ScreenshotTags.Any(t => t.TagId == tag));
        if (!string.IsNullOrWhiteSpace(f.Format)) { var fmt = f.Format.ToLowerInvariant(); q = q.Where(x => x.Format == fmt); }
        if (f.From is { } from) { var u = from.ToUniversalTime(); q = q.Where(x => x.CreatedAt >= u); }
        if (f.To is { } to) { var u = to.ToUniversalTime(); q = q.Where(x => x.CreatedAt < u); }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var pattern = "%" + EscapeLike(f.Search.Trim()) + "%";
            q = q.Where(x => EF.Functions.Like(x.FileName, pattern, "\\")
                || (x.SourceTitle != null && EF.Functions.Like(x.SourceTitle, pattern, "\\"))
                || (x.OcrText != null && EF.Functions.Like(x.OcrText, pattern, "\\")));
        }
        return q;
    }

    /// <summary>Escapes LIKE wildcards so user input such as '%' or '_' matches literally.</summary>
    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<IReadOnlyList<Screenshot>> QueryAsync(ScreenshotQuery query, PageCursor? after, int take, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = Filter(db.Screenshots.AsNoTracking(), query);
        if (after is { } c)
        {
            var at = c.CreatedAt.ToUniversalTime();
            q = q.Where(x => x.CreatedAt < at || (x.CreatedAt == at && x.Id < c.Id));
        }
        return await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(ScreenshotQuery query, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await Filter(db.Screenshots.AsNoTracking(), query).CountAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Screenshot>> ListAllAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Screenshots.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateFileInfoAsync(long id, string? thumbnailPath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Screenshots.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ThumbnailPath, thumbnailPath), ct).ConfigureAwait(false);
    }

    public async Task SetFavoriteAsync(long id, bool isFavorite, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Screenshots.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsFavorite, isFavorite), ct).ConfigureAwait(false);
    }

    public async Task SetOcrAsync(long id, string? text, string? language, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Screenshots.Where(x => x.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OcrText, text).SetProperty(x => x.OcrLanguage, language), ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var shot = await db.Screenshots.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (shot is null) return false;
        db.Screenshots.Remove(shot); // cascades to annotation document and tag links
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    public static string NormalizeTagName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("Tag name cannot be empty.", nameof(name));
        if (n.Length > 50) throw new ArgumentException("Tag name cannot be longer than 50 characters.", nameof(name));
        return n;
    }

    public async Task<Tag> GetOrCreateTagAsync(string name, CancellationToken ct = default)
    {
        var n = NormalizeTagName(name);
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var existing = await db.Tags.AsNoTracking().FirstOrDefaultAsync(t => t.Name == n, ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        var tag = new Tag { Name = n, CreatedAt = DateTime.UtcNow };
        db.Tags.Add(tag);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return tag;
        }
        catch (DbUpdateException)
        {
            // Lost a race with another creator of the same (case-insensitive) name.
            await using var db2 = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db2.Tags.AsNoTracking().FirstAsync(t => t.Name == n, ct).ConfigureAwait(false);
        }
    }

    public async Task AddTagAsync(long screenshotId, long tagId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await db.ScreenshotTags.AnyAsync(x => x.ScreenshotId == screenshotId && x.TagId == tagId, ct).ConfigureAwait(false)) return;
        db.ScreenshotTags.Add(new ScreenshotTag { ScreenshotId = screenshotId, TagId = tagId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveTagAsync(long screenshotId, long tagId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ScreenshotTags.Where(x => x.ScreenshotId == screenshotId && x.TagId == tagId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(long tagId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Tags.Where(x => x.Id == tagId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Tag>> GetTagsForAsync(long screenshotId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ScreenshotTags.AsNoTracking().Where(x => x.ScreenshotId == screenshotId)
            .Select(x => x.Tag!).OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<AnnotationDocument?> GetAnnotationDocumentAsync(long screenshotId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AnnotationDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.ScreenshotId == screenshotId, ct).ConfigureAwait(false);
    }

    public async Task SaveAnnotationDocumentAsync(long screenshotId, int version, string data, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var doc = await db.AnnotationDocuments.FirstOrDefaultAsync(x => x.ScreenshotId == screenshotId, ct).ConfigureAwait(false);
        if (doc is null)
            db.AnnotationDocuments.Add(new AnnotationDocument { ScreenshotId = screenshotId, DocumentVersion = version, DocumentData = data, UpdatedAt = DateTime.UtcNow });
        else
        {
            doc.DocumentVersion = version; doc.DocumentData = data; doc.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
