using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Snaply.Core.Interfaces;
using Snaply.Infrastructure.Persistence;
using Snaply.Infrastructure.Settings;
using Snaply.Infrastructure.Storage;

namespace Snaply.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddSnaplyInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton<IDbContextFactory<SnaplyDbContext>>(_ => new PathDbContextFactory(paths.DatabasePath));
        services.AddSingleton<DatabaseMigrator>();
        services.AddSingleton<IApplicationSettingsService, ApplicationSettingsService>();
        services.AddSingleton<IScreenshotRepository, ScreenshotRepository>();
        services.AddSingleton<ImageStorageService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<IScreenshotLibrary, ScreenshotLibrary>();
        return services;
    }

    private sealed class PathDbContextFactory : IDbContextFactory<SnaplyDbContext>
    {
        private readonly DbContextOptions<SnaplyDbContext> _options;
        public PathDbContextFactory(string path) => _options = DatabaseOptions.For(path);
        public SnaplyDbContext CreateDbContext() => new(_options);
    }
}
