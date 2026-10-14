using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Infrastructure.Persistence;

namespace Snaply.Infrastructure.Tests;

public class SourceTitleTests
{
    [Fact]
    public async Task A_capture_stores_the_trimmed_window_title()
    {
        using var env = new TestEnv();
        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region, "  Inbox - Mail  ");
        Assert.Equal("Inbox - Mail", r.Value.SourceTitle);
        Assert.Equal("Inbox - Mail", (await env.Repo.GetAsync(r.Value.Id))!.SourceTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_or_blank_titles_are_not_stored(string? title)
    {
        using var env = new TestEnv();
        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region, title);
        Assert.Null(r.Value.SourceTitle);
    }

    [Fact]
    public async Task Very_long_titles_are_cut_to_the_limit()
    {
        using var env = new TestEnv();
        var r = await env.Library.AddAsync(TestEnv.Image(), CaptureMethod.Region, new string('x', 500));
        Assert.Equal(Screenshot.MaxSourceTitleLength, r.Value.SourceTitle!.Length);
    }

    [Fact]
    public async Task Edited_copies_never_get_a_title_unless_one_is_given()
    {
        using var env = new TestEnv();
        var r = await env.Library.AddEncodedAsync(Snaply.Infrastructure.Storage.ImageCodec.Encode(TestEnv.Image(), ImageFormat.Png), "png", 64, 48, CaptureMethod.Edited);
        Assert.Null(r.Value.SourceTitle);
    }

    [Fact]
    public async Task Search_matches_the_window_title_as_well_as_file_name_and_text()
    {
        using var env = new TestEnv();
        var postman = (await env.Library.AddAsync(TestEnv.Image(seed: 1), CaptureMethod.Region, "Postman - Collections")).Value;
        var other = (await env.Library.AddAsync(TestEnv.Image(seed: 2), CaptureMethod.Region, "Telegram")).Value;
        var none = (await env.Library.AddAsync(TestEnv.Image(seed: 3), CaptureMethod.Region)).Value;

        var hits = await env.Repo.QueryAsync(new ScreenshotQuery(Search: "postman"), null, 50);
        Assert.Equal(new[] { postman.Id }, hits.Select(h => h.Id));
        Assert.Equal(1, await env.Repo.CountAsync(new ScreenshotQuery(Search: "tele")));
        Assert.DoesNotContain(none.Id, hits.Select(h => h.Id));
        Assert.NotEqual(postman.Id, other.Id);
    }

    [Fact]
    public async Task Search_treats_wildcards_in_the_query_literally()
    {
        using var env = new TestEnv();
        await env.Library.AddAsync(TestEnv.Image(seed: 1), CaptureMethod.Region, "Sales 100% report");
        await env.Library.AddAsync(TestEnv.Image(seed: 2), CaptureMethod.Region, "Sales 1000 report");
        Assert.Equal(1, await env.Repo.CountAsync(new ScreenshotQuery(Search: "100%")));
    }

    /// <summary>A database from before this feature keeps all rows; they simply have no title.</summary>
    [Fact]
    public async Task Upgrading_an_older_database_keeps_existing_screenshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "snaply-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            var options = DatabaseOptions.For(paths.DatabasePath);

            await using (var old = new SnaplyDbContext(options))
            {
                await old.GetService<IMigrator>().MigrateAsync("InitialCreate");     // the schema before SourceTitle existed
                await old.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Screenshots (FilePath, FileName, CreatedAt, Width, Height, Format, FileSize, IsFavorite, CreatedByCaptureMethod) " +
                    "VALUES ('C:/x/old.png', 'old.png', '2026-01-01 10:00:00', 10, 10, 'png', 1, 0, 'Region')");
            }

            var factory = new UpgradeFactory(paths.DatabasePath);
            await new DatabaseMigrator(paths, factory, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

            var repo = new ScreenshotRepository(factory);
            var all = await repo.ListAllAsync();
            var row = Assert.Single(all);
            Assert.Equal("old.png", row.FileName);
            Assert.Null(row.SourceTitle);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

internal sealed class UpgradeFactory : IDbContextFactory<SnaplyDbContext>
{
    private readonly DbContextOptions<SnaplyDbContext> _options;
    public UpgradeFactory(string path) => _options = DatabaseOptions.For(path);
    public SnaplyDbContext CreateDbContext() => new(_options);
}
