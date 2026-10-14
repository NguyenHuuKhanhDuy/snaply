using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Infrastructure;
using Snaply.Infrastructure.Persistence;
using Snaply.Infrastructure.Settings;
using Snaply.Infrastructure.Storage;

namespace Snaply.Infrastructure.Tests;

public sealed class TestEnv : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "snaply-tests-" + Guid.NewGuid().ToString("N"));
    public AppPaths Paths { get; }
    public IDbContextFactory<SnaplyDbContext> Factory { get; }
    public ScreenshotRepository Repo { get; }
    public ApplicationSettingsService Settings { get; }
    public ScreenshotLibrary Library { get; }

    public TestEnv()
    {
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
        Factory = new TestFactory(Paths.DatabasePath);
        new DatabaseMigrator(Paths, Factory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync().GetAwaiter().GetResult();
        Repo = new ScreenshotRepository(Factory);
        Settings = new ApplicationSettingsService(Factory, Paths, NullLogger<ApplicationSettingsService>.Instance);
        Settings.LoadAsync().GetAwaiter().GetResult();
        Library = new ScreenshotLibrary(Repo, new ImageStorageService(), new ThumbnailService(Paths), Settings, Paths, NullLogger<ScreenshotLibrary>.Instance);
    }

    public static RawImage Image(int w = 64, int h = 48, byte seed = 40)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { px[i * 4] = (byte)(i % 251); px[i * 4 + 1] = seed; px[i * 4 + 2] = (byte)(i / w); px[i * 4 + 3] = 255; }
        return new RawImage(w, h, px);
    }

    public static Screenshot Shot(string name, DateTime? at = null) => new()
    {
        FilePath = "C:/shots/" + name, FileName = name, CreatedAt = at ?? DateTime.UtcNow, Width = 10, Height = 10, Format = "png", FileSize = 1,
    };

    public void Dispose()
    {
        SqliteClear();
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class TestFactory : IDbContextFactory<SnaplyDbContext>
    {
        private readonly DbContextOptions<SnaplyDbContext> _o;
        public TestFactory(string path) => _o = DatabaseOptions.For(path);
        public SnaplyDbContext CreateDbContext() => new(_o);
    }
}

public class MigrationTests
{
    [Fact]
    public async Task Fresh_database_is_created_with_schema()
    {
        using var env = new TestEnv();
        await using var db = await env.Factory.CreateDbContextAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(File.Exists(env.Paths.DatabasePath));
        Assert.False(Directory.Exists(env.Paths.BackupsDirectory), "no backup for first creation");
    }

    [Fact]
    public async Task Migrating_twice_is_idempotent()
    {
        using var env = new TestEnv();
        await new DatabaseMigrator(env.Paths, env.Factory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        await env.Repo.AddAsync(TestEnv.Shot("a.png"));
        await new DatabaseMigrator(env.Paths, env.Factory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();
        Assert.Equal(1, await env.Repo.CountAsync(new ScreenshotQuery()));
    }

    [Fact]
    public async Task Database_from_newer_version_is_refused()
    {
        using var env = new TestEnv();
        await using (var db = await env.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('29990101000000_FromTheFuture','99.0.0')");
        await Assert.ThrowsAsync<SchemaTooNewException>(() =>
            new DatabaseMigrator(env.Paths, env.Factory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync());
    }

    [Fact]
    public async Task Unrecognized_history_row_is_refused_as_newer_schema()
    {
        using var env = new TestEnv();
        // Simulate an older schema by removing the (only) recorded migration but keeping the file.
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ScreenshotTags\"; DROP TABLE \"AnnotationDocuments\"; DROP TABLE \"Tags\"; DROP TABLE \"Screenshots\"; DROP TABLE \"ApplicationSettings\";");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\",\"ProductVersion\") VALUES ('20000101000000_Old','8.0.0')");
        }
        // Old migration id unknown to this build => treated as newer; this guards the "refuse" path, not the backup path.
        await Assert.ThrowsAsync<SchemaTooNewException>(() =>
            new DatabaseMigrator(env.Paths, env.Factory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync());
    }
}

public class ConstraintTests
{
    [Fact]
    public async Task Duplicate_file_path_is_rejected_by_database()
    {
        using var env = new TestEnv();
        await using var db = await env.Factory.CreateDbContextAsync();
        db.Screenshots.Add(TestEnv.Shot("dup.png"));
        db.Screenshots.Add(TestEnv.Shot("dup.png"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Tag_names_unique_case_insensitively_in_database()
    {
        using var env = new TestEnv();
        await using var db = await env.Factory.CreateDbContextAsync();
        db.Tags.Add(new Tag { Name = "Work", CreatedAt = DateTime.UtcNow });
        db.Tags.Add(new Tag { Name = "work", CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_screenshot_cascades_to_annotations_and_tag_links()
    {
        using var env = new TestEnv();
        var s = await env.Repo.AddAsync(TestEnv.Shot("c.png"));
        var tag = await env.Repo.GetOrCreateTagAsync("x");
        await env.Repo.AddTagAsync(s.Id, tag.Id);
        await env.Repo.SaveAnnotationDocumentAsync(s.Id, 1, "{}");

        Assert.True(await env.Repo.DeleteAsync(s.Id));

        await using var db = await env.Factory.CreateDbContextAsync();
        Assert.Equal(0, await db.AnnotationDocuments.CountAsync());
        Assert.Equal(0, await db.ScreenshotTags.CountAsync());
        Assert.Equal(1, await db.Tags.CountAsync()); // the tag itself survives
    }

    [Fact]
    public async Task Metadata_round_trips_including_utc_timestamps()
    {
        using var env = new TestEnv();
        var at = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var s = TestEnv.Shot("m.png", at);
        s.Width = 1920; s.Height = 1080; s.FileSize = 12345; s.IsFavorite = true; s.OcrText = "Xin chào"; s.CreatedByCaptureMethod = CaptureMethod.Region;
        await env.Repo.AddAsync(s);

        var back = await env.Repo.GetAsync(s.Id);
        Assert.NotNull(back);
        Assert.Equal(at, back!.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, back.CreatedAt.Kind);
        Assert.Equal((1920, 1080, 12345L, true, "Xin chào", CaptureMethod.Region), (back.Width, back.Height, back.FileSize, back.IsFavorite, back.OcrText, back.CreatedByCaptureMethod));
    }
}

public class RepositoryTests
{
    [Fact]
    public async Task Add_is_idempotent_per_path()
    {
        using var env = new TestEnv();
        var a = await env.Repo.AddAsync(TestEnv.Shot("same.png"));
        var b = await env.Repo.AddAsync(TestEnv.Shot("same.png"));
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, await env.Repo.CountAsync(new ScreenshotQuery()));
    }

    [Fact]
    public async Task Query_orders_newest_first_with_keyset_paging()
    {
        using var env = new TestEnv();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 25; i++) await env.Repo.AddAsync(TestEnv.Shot($"s{i:00}.png", t0.AddMinutes(i)));
        // Two with identical timestamps exercise the Id tiebreaker.
        await env.Repo.AddAsync(TestEnv.Shot("tie1.png", t0.AddMinutes(100)));
        await env.Repo.AddAsync(TestEnv.Shot("tie2.png", t0.AddMinutes(100)));

        var seen = new List<string>();
        PageCursor? cursor = null;
        while (true)
        {
            var page = await env.Repo.QueryAsync(new ScreenshotQuery(), cursor, 10);
            if (page.Count == 0) break;
            seen.AddRange(page.Select(p => p.FileName));
            cursor = new PageCursor(page[^1].CreatedAt, page[^1].Id);
        }

        Assert.Equal(27, seen.Count);
        Assert.Equal(27, seen.Distinct().Count());
        Assert.Equal(new[] { "tie2.png", "tie1.png", "s24.png" }, seen.Take(3));
        Assert.Equal("s00.png", seen[^1]);
    }

    [Fact]
    public async Task Search_by_filename_is_case_insensitive_and_literal_for_wildcards()
    {
        using var env = new TestEnv();
        await env.Repo.AddAsync(TestEnv.Shot("Invoice_2026.png"));
        await env.Repo.AddAsync(TestEnv.Shot("holiday.png"));
        await env.Repo.AddAsync(TestEnv.Shot("100%_done.png"));

        Assert.Equal(new[] { "Invoice_2026.png" }, (await env.Repo.QueryAsync(new ScreenshotQuery(Search: "invoice"), null, 10)).Select(s => s.FileName));
        Assert.Equal(new[] { "100%_done.png" }, (await env.Repo.QueryAsync(new ScreenshotQuery(Search: "%_"), null, 10)).Select(s => s.FileName));
        Assert.Empty(await env.Repo.QueryAsync(new ScreenshotQuery(Search: "%"), null, 10).ContinueWith(t => t.Result.Where(s => s.FileName == "holiday.png")));
        Assert.Single(await env.Repo.QueryAsync(new ScreenshotQuery(Search: "_20"), null, 10));
    }

    [Fact]
    public async Task Search_matches_ocr_text()
    {
        using var env = new TestEnv();
        var a = await env.Repo.AddAsync(TestEnv.Shot("a.png"));
        await env.Repo.AddAsync(TestEnv.Shot("b.png"));
        await env.Repo.SetOcrAsync(a.Id, "Total amount due: 42", "eng");

        var hits = await env.Repo.QueryAsync(new ScreenshotQuery(Search: "amount due"), null, 10);
        Assert.Equal("a.png", Assert.Single(hits).FileName);
    }

    [Fact]
    public async Task Filters_by_favorite_format_date_and_tag()
    {
        using var env = new TestEnv();
        var t0 = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = await env.Repo.AddAsync(TestEnv.Shot("a.png", t0));
        var jpg = TestEnv.Shot("b.jpg", t0.AddDays(5)); jpg.Format = "jpg";
        var b = await env.Repo.AddAsync(jpg);
        await env.Repo.SetFavoriteAsync(b.Id, true);
        var tag = await env.Repo.GetOrCreateTagAsync("Receipts");
        await env.Repo.AddTagAsync(a.Id, tag.Id);

        Assert.Equal("b.jpg", Assert.Single(await env.Repo.QueryAsync(new ScreenshotQuery(FavoritesOnly: true), null, 10)).FileName);
        Assert.Equal("b.jpg", Assert.Single(await env.Repo.QueryAsync(new ScreenshotQuery(Format: "JPG"), null, 10)).FileName);
        Assert.Equal("a.png", Assert.Single(await env.Repo.QueryAsync(new ScreenshotQuery(From: t0.AddDays(-1), To: t0.AddDays(2)), null, 10)).FileName);
        Assert.Equal("a.png", Assert.Single(await env.Repo.QueryAsync(new ScreenshotQuery(TagId: tag.Id), null, 10)).FileName);
    }

    [Fact]
    public async Task Tags_reuse_existing_name_ignoring_case_and_validate_input()
    {
        using var env = new TestEnv();
        var a = await env.Repo.GetOrCreateTagAsync("Design");
        var b = await env.Repo.GetOrCreateTagAsync("  design ");
        Assert.Equal(a.Id, b.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => env.Repo.GetOrCreateTagAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Repo.GetOrCreateTagAsync(new string('x', 51)));
        Assert.Single(await env.Repo.ListTagsAsync());
    }

    [Fact]
    public async Task Assign_remove_and_delete_tags()
    {
        using var env = new TestEnv();
        var s = await env.Repo.AddAsync(TestEnv.Shot("t.png"));
        var t = await env.Repo.GetOrCreateTagAsync("one");
        await env.Repo.AddTagAsync(s.Id, t.Id);
        await env.Repo.AddTagAsync(s.Id, t.Id); // duplicate ignored
        Assert.Single(await env.Repo.GetTagsForAsync(s.Id));
        await env.Repo.RemoveTagAsync(s.Id, t.Id);
        Assert.Empty(await env.Repo.GetTagsForAsync(s.Id));
        await env.Repo.AddTagAsync(s.Id, t.Id);
        await env.Repo.DeleteTagAsync(t.Id);
        Assert.Empty(await env.Repo.GetTagsForAsync(s.Id));
    }

    [Fact]
    public async Task Annotation_document_upserts()
    {
        using var env = new TestEnv();
        var s = await env.Repo.AddAsync(TestEnv.Shot("d.png"));
        await env.Repo.SaveAnnotationDocumentAsync(s.Id, 1, "{\"a\":1}");
        await env.Repo.SaveAnnotationDocumentAsync(s.Id, 1, "{\"a\":2}");
        var doc = await env.Repo.GetAnnotationDocumentAsync(s.Id);
        Assert.Equal("{\"a\":2}", doc!.DocumentData);
        await using var db = await env.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.AnnotationDocuments.CountAsync());
    }

    [Fact]
    public async Task Delete_missing_returns_false()
    {
        using var env = new TestEnv();
        Assert.False(await env.Repo.DeleteAsync(12345));
    }
}

public class SettingsTests
{
    [Fact]
    public async Task Defaults_when_nothing_stored()
    {
        using var env = new TestEnv();
        Assert.Equal(ThemeMode.System, env.Settings.Current.Theme);
        Assert.Equal(env.Paths.DefaultScreenshotsDirectory, env.Settings.EffectiveSaveDirectory);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Changes_persist_across_a_new_service_instance()
    {
        using var env = new TestEnv();
        var dir = Path.Combine(env.Root, "my shots");
        var r = await env.Settings.UpdateAsync(s =>
        {
            s.Theme = ThemeMode.Dark; s.SaveDirectory = dir; s.DefaultFormat = ImageFormat.Jpeg;
            s.CaptureDelaySeconds = 3; s.OcrLanguages = new() { "eng", "vie" };
            s.Hotkeys[HotkeyActions.CaptureRegion] = "Ctrl+Alt+R";
        });
        Assert.True(r.IsSuccess, r.Error?.Message);

        var fresh = new ApplicationSettingsService(env.Factory, env.Paths, NullLogger<ApplicationSettingsService>.Instance);
        await fresh.LoadAsync();
        Assert.Equal(ThemeMode.Dark, fresh.Current.Theme);
        Assert.Equal(dir, fresh.Current.SaveDirectory);
        Assert.Equal(ImageFormat.Jpeg, fresh.Current.DefaultFormat);
        Assert.Equal(3, fresh.Current.CaptureDelaySeconds);
        Assert.Equal(new[] { "eng", "vie" }, fresh.Current.OcrLanguages);
        Assert.Equal("Ctrl+Alt+R", fresh.Current.Hotkeys[HotkeyActions.CaptureRegion]);
        Assert.Equal(dir, fresh.EffectiveSaveDirectory);
    }

    [Fact]
    public async Task Invalid_update_is_rejected_and_state_unchanged()
    {
        using var env = new TestEnv();
        var raised = 0;
        env.Settings.Changed += (_, _) => raised++;
        var r = await env.Settings.UpdateAsync(s => { s.Theme = ThemeMode.Dark; s.CaptureDelaySeconds = 999; });
        Assert.False(r.IsSuccess);
        Assert.Equal(ThemeMode.System, env.Settings.Current.Theme);
        Assert.Equal(0, raised);
        var fresh = new ApplicationSettingsService(env.Factory, env.Paths, NullLogger<ApplicationSettingsService>.Instance);
        await fresh.LoadAsync();
        Assert.Equal(ThemeMode.System, fresh.Current.Theme);
    }

    [Fact]
    public async Task Unwritable_directory_is_rejected_and_previous_stays()
    {
        using var env = new TestEnv();
        var blocker = Path.Combine(env.Root, "iamafile");
        await File.WriteAllTextAsync(blocker, "x");
        var r = await env.Settings.UpdateAsync(s => s.SaveDirectory = Path.Combine(blocker, "sub"));
        Assert.Equal(ErrorCode.StorageUnavailable, r.Error!.Code);
        Assert.Null(env.Settings.Current.SaveDirectory);
    }

    [Fact]
    public async Task Corrupt_stored_values_fall_back_to_defaults()
    {
        using var env = new TestEnv();
        await env.Settings.UpdateAsync(s => s.Theme = ThemeMode.Light);
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE ApplicationSettings SET Value='\"Neon\"' WHERE Key='Theme'");
            await db.Database.ExecuteSqlRawAsync("UPDATE ApplicationSettings SET Value='not json' WHERE Key='CaptureDelaySeconds'");
            await db.Database.ExecuteSqlRawAsync("UPDATE ApplicationSettings SET Value='{{\"CaptureRegion\":\"nope\"}}' WHERE Key='Hotkeys'");
        }
        var fresh = new ApplicationSettingsService(env.Factory, env.Paths, NullLogger<ApplicationSettingsService>.Instance);
        await fresh.LoadAsync();
        Assert.Equal(ThemeMode.System, fresh.Current.Theme);
        Assert.Equal(0, fresh.Current.CaptureDelaySeconds);
        Assert.Equal("Alt+Shift+S", fresh.Current.Hotkeys[HotkeyActions.CaptureRegion]);
    }

    [Fact]
    public async Task Changed_event_fires_after_valid_update()
    {
        using var env = new TestEnv();
        AppSettings? seen = null;
        env.Settings.Changed += (_, s) => seen = s;
        await env.Settings.UpdateAsync(s => s.ReduceMotion = true);
        Assert.True(seen!.ReduceMotion);
    }
}

public class LibraryTests
{
    [Fact]
    public async Task Add_writes_file_thumbnail_and_row()
    {
        using var env = new TestEnv();
        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.FullScreen);
        Assert.True(r.IsSuccess, r.Error?.Message);
        var s = r.Value;
        Assert.True(File.Exists(s.FilePath));
        Assert.True(File.Exists(s.ThumbnailPath));
        Assert.StartsWith(env.Paths.DefaultScreenshotsDirectory, s.FilePath);
        Assert.Equal((64, 48, "png"), (s.Width, s.Height, s.Format));
        Assert.Equal(new FileInfo(s.FilePath).Length, s.FileSize);
        var dims = ImageCodec.ReadSize(s.FilePath)!.Value;
        Assert.Equal((64, 48), dims);
        Assert.Equal(1, await env.Repo.CountAsync(new ScreenshotQuery()));
    }

    [Fact]
    public async Task Uses_configured_format_and_directory()
    {
        using var env = new TestEnv();
        var dir = Path.Combine(env.Root, "custom");
        await env.Settings.UpdateAsync(s => { s.SaveDirectory = dir; s.DefaultFormat = ImageFormat.Jpeg; });
        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region);
        Assert.StartsWith(dir, r.Value.FilePath);
        Assert.EndsWith(".jpg", r.Value.FilePath);
        Assert.Equal("jpg", r.Value.Format);
    }

    [Fact]
    public async Task Rapid_adds_get_unique_files()
    {
        using var env = new TestEnv();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region)));
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(20, results.Select(r => r.Value.FilePath).Distinct().Count());
        Assert.Equal(20, Directory.GetFiles(env.Paths.DefaultScreenshotsDirectory).Length);
    }

    [Fact]
    public async Task Unwritable_target_fails_cleanly_without_rows_or_temp_files()
    {
        using var env = new TestEnv();
        var blocker = Path.Combine(env.Root, "blocker");
        await File.WriteAllTextAsync(blocker, "x");
        // Bypass validation by pointing settings at a directory that is later replaced by a file.
        var dir = Path.Combine(env.Root, "gone");
        await env.Settings.UpdateAsync(s => s.SaveDirectory = dir);
        Directory.Delete(dir);
        await File.WriteAllTextAsync(dir, "now a file");

        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region);
        Assert.Equal(ErrorCode.StorageUnavailable, r.Error!.Code);
        Assert.Equal(0, await env.Repo.CountAsync(new ScreenshotQuery()));
    }

    [Fact]
    public async Task Database_failure_after_write_removes_created_files()
    {
        using var env = new TestEnv();
        // Dropping the table makes the insert fail after the file and thumbnail were already written.
        await using (var db = await env.Factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlRawAsync("DROP TABLE ScreenshotTags; DROP TABLE AnnotationDocuments; DROP TABLE Screenshots;");

        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region);
        Assert.False(r.IsSuccess);
        Assert.Empty(Directory.GetFiles(env.Paths.DefaultScreenshotsDirectory));
        Assert.Empty(Directory.GetFiles(env.Paths.ThumbnailsDirectory));
    }

    [Fact]
    public async Task Delete_removes_row_file_and_thumbnail()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region)).Value;
        var r = await env.Library.DeleteAsync(s.Id);
        Assert.True(r.IsSuccess);
        Assert.False(File.Exists(s.FilePath));
        Assert.False(File.Exists(s.ThumbnailPath));
        Assert.Null(await env.Repo.GetAsync(s.Id));
    }

    [Fact]
    public async Task Delete_reports_locked_file_instead_of_hiding_it()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region)).Value;
        using var lockHandle = new FileStream(s.FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var r = await env.Library.DeleteAsync(s.Id);
        Assert.Equal(ErrorCode.StorageUnavailable, r.Error!.Code);
        Assert.Contains(s.FileName, r.Error.Message);
        Assert.Null(await env.Repo.GetAsync(s.Id)); // row is gone; file is reported and later found as an orphan
        lockHandle.Dispose();
        var v = await env.Library.VerifyAsync();
        Assert.Contains(s.FilePath, v.OrphanImageFiles);
    }

    [Fact]
    public async Task Delete_unknown_returns_not_found()
    {
        using var env = new TestEnv();
        Assert.Equal(ErrorCode.NotFound, (await env.Library.DeleteAsync(999)).Error!.Code);
    }

    [Fact]
    public async Task Missing_thumbnail_is_regenerated()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region)).Value;
        File.Delete(s.ThumbnailPath!);
        var r = await env.Library.EnsureThumbnailAsync(s);
        Assert.True(r.IsSuccess);
        Assert.True(File.Exists(s.ThumbnailPath));
    }

    [Fact]
    public async Task Thumbnail_for_missing_image_reports_not_found()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region)).Value;
        File.Delete(s.FilePath); File.Delete(s.ThumbnailPath!);
        Assert.Equal(ErrorCode.NotFound, (await env.Library.EnsureThumbnailAsync(s)).Error!.Code);
    }

    [Fact]
    public async Task Old_low_resolution_thumbnails_are_regenerated_on_demand()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(1600, 800), CaptureMethod.Region)).Value;
        var old = Path.Combine(env.Paths.ThumbnailsDirectory, "legacy.thumb.jpg");
        File.Copy(s.ThumbnailPath!, old);
        s.ThumbnailPath = old;

        Assert.True((await env.Library.EnsureThumbnailAsync(s)).IsSuccess);

        Assert.True(ThumbnailService.IsCurrent(s.ThumbnailPath));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task Thumbnails_are_bounded_to_max_edge_preserving_aspect()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(1600, 800), CaptureMethod.Region)).Value;
        var (w, h) = ImageCodec.ReadSize(s.ThumbnailPath!)!.Value;
        Assert.Equal((640, 320), (w, h));
    }

    [Fact]
    public async Task Small_images_are_not_upscaled()
    {
        using var env = new TestEnv();
        var s = (await env.Library.AddAsync(TestEnv.Image(40, 30), CaptureMethod.Region)).Value;
        Assert.Equal((40, 30), ImageCodec.ReadSize(s.ThumbnailPath!)!.Value);
    }

    [Fact]
    public async Task Verify_finds_missing_files_and_orphans_and_cleanup_only_removes_orphans()
    {
        using var env = new TestEnv();
        var keep = (await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region)).Value;
        var lost = (await env.Library.AddAsync(TestEnv.Image(seed: 99), CaptureMethod.Region)).Value;
        File.Delete(lost.FilePath);

        var orphanImage = Path.Combine(env.Paths.DefaultScreenshotsDirectory, "Snaply_orphan.png");
        var unrelated = Path.Combine(env.Paths.DefaultScreenshotsDirectory, "holiday-photo.png");
        var orphanThumb = Path.Combine(env.Paths.ThumbnailsDirectory, "stale.thumb.jpg");
        await File.WriteAllBytesAsync(orphanImage, new byte[] { 1 });
        await File.WriteAllBytesAsync(unrelated, new byte[] { 1 });
        await File.WriteAllBytesAsync(orphanThumb, new byte[] { 1 });

        var v = await env.Library.VerifyAsync();
        Assert.Equal(new[] { lost.Id }, v.MissingFileScreenshotIds);
        Assert.Equal(new[] { orphanImage }, v.OrphanImageFiles);
        Assert.Equal(new[] { orphanThumb }, v.OrphanThumbnails);

        var removed = await env.Library.DeleteOrphansAsync(v);
        Assert.Equal(2, removed);
        Assert.True(File.Exists(keep.FilePath));
        Assert.True(File.Exists(unrelated), "files Snaply did not create are never touched");
        Assert.NotNull(await env.Repo.GetAsync(lost.Id)); // missing-file rows are only reported, never silently deleted
    }

    [Fact]
    public async Task Encoded_add_rejects_unknown_extension()
    {
        using var env = new TestEnv();
        var r = await env.Library.AddEncodedAsync(new byte[] { 1 }, "bmp", 1, 1, CaptureMethod.Edited);
        Assert.Equal(ErrorCode.InvalidInput, r.Error!.Code);
    }
}

public class StorageTests
{
    [Fact]
    public async Task Export_refuses_overwrite_unless_allowed_and_leaves_no_temp_files()
    {
        using var env = new TestEnv();
        var svc = new ImageStorageService();
        var path = Path.Combine(env.Root, "out", "e.png");
        Assert.True((await svc.ExportAsync(new byte[] { 1, 2, 3 }, path, overwrite: false)).IsSuccess);

        var again = await svc.ExportAsync(new byte[] { 9 }, path, overwrite: false);
        Assert.Equal(ErrorCode.Conflict, again.Error!.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));

        Assert.True((await svc.ExportAsync(new byte[] { 9 }, path, overwrite: true)).IsSuccess);
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Export_rejects_relative_paths()
    {
        var r = await new ImageStorageService().ExportAsync(new byte[] { 1 }, "relative.png", true);
        Assert.Equal(ErrorCode.InvalidInput, r.Error!.Code);
    }

    [Fact]
    public async Task Save_rejects_relative_directory()
    {
        var r = await new ImageStorageService().SaveAsync(new byte[] { 1 }, "png", "relative");
        Assert.Equal(ErrorCode.StorageUnavailable, r.Error!.Code);
    }

    [Fact]
    public void Generated_names_are_unique_and_safe()
    {
        var names = Enumerable.Range(0, 500).Select(_ => ImageStorageService.NewFileName("PNG")).ToList();
        Assert.Equal(500, names.Distinct().Count());
        Assert.All(names, n => { Assert.StartsWith("Snaply_", n); Assert.EndsWith(".png", n); Assert.True(n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0); });
    }

    [Fact]
    public void Decode_rejects_garbage_files()
    {
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, "this is not an image");
            Assert.Null(ImageCodec.DecodeSafe(f));
            Assert.Null(ImageCodec.ReadSize(f));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Encode_png_round_trips_pixels_exactly()
    {
        var img = TestEnv.Image(20, 10);
        var bytes = ImageCodec.Encode(img, ImageFormat.Png);
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(f, bytes);
            using var bmp = ImageCodec.DecodeSafe(f)!;
            Assert.Equal((20, 10), (bmp.Width, bmp.Height));
            Assert.Equal(img.Bgra[0], bmp.GetPixel(0, 0).Blue);
            Assert.Equal(img.Bgra[1], bmp.GetPixel(0, 0).Green);
        }
        finally { File.Delete(f); }
    }
}
