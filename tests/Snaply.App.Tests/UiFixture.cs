using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SkiaSharp;
using Snaply.App.Services;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;
using Snaply.Infrastructure;
using Snaply.Infrastructure.Persistence;

namespace Snaply.App.Tests;

/// <summary>
/// Real application services over a temporary database and synthetic images, with fakes for everything that touches the OS,
/// OCR and the network. Never reads the user's library or settings.
/// </summary>
public sealed class UiFixture : IDisposable
{
    private UiFixture(string root, ServiceProvider services) { Root = root; Services = services; }

    public string Root { get; }
    public ServiceProvider Services { get; }
    public IReadOnlyList<long> ScreenshotIds { get; private set; } = Array.Empty<long>();

    public static async Task<UiFixture> CreateAsync(bool populated, bool updateAvailable)
    {
        var root = Path.Combine(Path.GetTempPath(), "snaply-ui-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();

        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        s.AddSnaplyInfrastructure(paths);
        s.AddSnaplyApplication(paths);

        // ---- fakes (registered last so they win) ----
        var capture = Substitute.For<IScreenCaptureService>();
        capture.Support.Returns(new CaptureSupport(true));
        capture.GetMonitors().Returns(new[] { new MonitorInfo(0, new PixelRect(0, 0, 1920, 1080), 1, true) });
        s.Replace(ServiceDescriptor.Singleton(capture));

        s.Replace(ServiceDescriptor.Singleton(Substitute.For<IClipboardService>()));

        var hotkeys = Substitute.For<IGlobalHotkeyService>();
        hotkeys.IsSupported.Returns(true);
        hotkeys.Register(Arg.Any<string>(), Arg.Any<HotkeyGesture>(), Arg.Any<Action>()).Returns(new HotkeyRegistration(HotkeyStatus.Registered));
        s.Replace(ServiceDescriptor.Singleton(hotkeys));

        var shell = Substitute.For<IShellService>();
        shell.CanRevealInFolder.Returns(true);
        s.Replace(ServiceDescriptor.Singleton(shell));

        var ocr = Substitute.For<IOcrService>();
        ocr.EngineName.Returns("Tesseract");
        ocr.IsAvailable.Returns(true);
        ocr.GetInstalledLanguages().Returns(new[] { "eng", "vie" });
        s.Replace(ServiceDescriptor.Singleton(ocr));

        var update = Substitute.For<IAppUpdateService>();
        update.IsSupported.Returns(true);
        update.CurrentVersion.Returns("0.1.1");
        update.CheckAsync(Arg.Any<CancellationToken>()).Returns(updateAvailable
            ? Result<UpdateInfo?>.Ok(new UpdateInfo("0.2.0", "- Faster capture\n- Settings navigation", new object()))
            : Result<UpdateInfo?>.Ok(null));
        s.Replace(ServiceDescriptor.Singleton(update));

        var provider = s.BuildServiceProvider();
        var fx = new UiFixture(root, provider);

        await provider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
        await provider.GetRequiredService<IApplicationSettingsService>().LoadAsync();
        provider.GetRequiredService<HotkeyManager>().ApplyAll();
        if (populated) await fx.PopulateAsync();
        if (updateAvailable) await provider.GetRequiredService<UpdateCoordinator>().CheckAsync(manual: true);
        return fx;
    }

    private async Task PopulateAsync()
    {
        var library = Services.GetRequiredService<IScreenshotLibrary>();
        var repo = Services.GetRequiredService<IScreenshotRepository>();
        var ids = new List<long>();
        var sizes = new[] { (1280, 720), (1920, 1080), (900, 600), (1440, 900), (800, 800), (1600, 900), (1024, 640), (1920, 1080), (1280, 800), (700, 500), (1366, 768), (1500, 1000) };

        for (var i = 0; i < sizes.Length; i++)
        {
            var (w, h) = sizes[i];
            var r = await library.AddAsync(Sample(i, w, h), i % 3 == 0 ? CaptureMethod.FullScreen : CaptureMethod.Region);
            ids.Add(r.Value.Id);
        }

        await repo.SetFavoriteAsync(ids[0], true);
        await repo.SetFavoriteAsync(ids[3], true);
        var design = await repo.GetOrCreateTagAsync("Design");
        var bugs = await repo.GetOrCreateTagAsync("Bugs");
        await repo.AddTagAsync(ids[0], design.Id);
        await repo.AddTagAsync(ids[1], design.Id);
        await repo.AddTagAsync(ids[2], bugs.Id);
        await repo.SetOcrAsync(ids[0], "Quarterly report\nRevenue grew 12% year over year.", "eng");
        ScreenshotIds = ids;
    }

    /// <summary>A synthetic "screenshot": gradient background, window-like panels and some text. Contains no real content.</summary>
    public static RawImage Sample(int index, int width, int height)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var c = new SKCanvas(bmp);
        var hue = (index * 37) % 360;
        using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
            new[] { SKColor.FromHsv(hue, 40, 95), SKColor.FromHsv((hue + 40) % 360, 55, 80) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, width, height, bg);

        using var panel = new SKPaint { Color = new SKColor(255, 255, 255, 235), IsAntialias = true };
        using var bar = new SKPaint { Color = new SKColor(40, 44, 60, 255), IsAntialias = true };
        using var text = new SKPaint { Color = new SKColor(40, 44, 60), IsAntialias = true, TextSize = Math.Max(14, height / 28f) };
        var pad = width / 12f;
        c.DrawRoundRect(new SKRect(pad, pad, width - pad, height - pad), 14, 14, panel);
        c.DrawRoundRect(new SKRect(pad, pad, width - pad, pad + height / 12f), 14, 14, bar);
        for (var i = 0; i < 5; i++)
            c.DrawText($"Sample content line {index + 1}.{i + 1}", pad * 1.4f, pad + height / 12f + (i + 1) * text.TextSize * 1.8f, text);
        using var accent = new SKPaint { Color = SKColor.FromHsv((hue + 180) % 360, 70, 90), IsAntialias = true };
        c.DrawCircle(width - pad * 2.2f, height - pad * 2.2f, Math.Min(width, height) / 9f, accent);
        return new RawImage(width, height, bmp.Bytes);
    }

    public void Dispose()
    {
        Services.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, true); } catch { /* best effort */ }
    }
}
