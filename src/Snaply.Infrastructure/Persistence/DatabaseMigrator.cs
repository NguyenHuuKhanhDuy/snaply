using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Snaply.Infrastructure.Persistence;

public sealed class SchemaTooNewException : Exception
{
    public SchemaTooNewException(string migration)
        : base($"The database was created by a newer version of Snaply (migration '{migration}'). Update Snaply to open it.") { }
}

public static class DatabaseOptions
{
    public static DbContextOptions<SnaplyDbContext> For(string databasePath) =>
        new DbContextOptionsBuilder<SnaplyDbContext>()
            .UseSqlite($"Data Source={databasePath};Foreign Keys=True;Pooling=False")
            .Options;
}

public sealed class DatabaseMigrator
{
    private readonly AppPaths _paths;
    private readonly IDbContextFactory<SnaplyDbContext> _factory;
    private readonly ILogger<DatabaseMigrator> _log;

    public DatabaseMigrator(AppPaths paths, IDbContextFactory<SnaplyDbContext> factory, ILogger<DatabaseMigrator> log)
    {
        _paths = paths; _factory = factory; _log = log;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        _paths.EnsureCreated();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var known = db.Database.GetMigrations().ToHashSet();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();

        var newer = applied.FirstOrDefault(m => !known.Contains(m));
        if (newer is not null) throw new SchemaTooNewException(newer);

        var pending = (await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (pending.Count == 0) return;

        // Back up only when upgrading an existing database, never for the first creation.
        if (applied.Count > 0 && File.Exists(_paths.DatabasePath))
        {
            Directory.CreateDirectory(_paths.BackupsDirectory);
            var backup = Path.Combine(_paths.BackupsDirectory, $"snaply-{DateTime.UtcNow:yyyyMMdd-HHmmss}-pre-{pending[0]}.db");
            File.Copy(_paths.DatabasePath, backup, overwrite: false);
            _log.LogInformation("Database backed up to {Backup} before applying {Count} migration(s)", backup, pending.Count);
        }

        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Database migrated: {Migrations}", string.Join(", ", pending));
    }
}
