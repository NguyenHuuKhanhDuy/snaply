using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.Core.Interfaces;
using Snaply.Core.Services;
using Snaply.Infrastructure;
using Snaply.Ocr;

namespace Snaply.App;

/// <summary>
/// Registers the application's own services and view models. Shared by the real app and the UI test harness, which
/// replaces platform, OCR and update services with fakes after calling this.
/// </summary>
public static class AppComposition
{
    public static IServiceCollection AddSnaplyApplication(this IServiceCollection s, AppPaths paths)
    {
        s.AddSingleton<NotificationService>();
        s.AddSingleton<INotificationService>(p => p.GetRequiredService<NotificationService>());
        s.AddSingleton<DialogService>();
        s.AddSingleton<IDialogService>(p => p.GetRequiredService<DialogService>());
        s.AddSingleton<ThemeService>();
        s.AddSingleton<LibraryEvents>();
        s.AddSingleton<NavigationHub>();
        s.AddSingleton<IAppWindowController, AppWindowController>();
        s.AddSingleton<IRegionSelector, RegionSelector>();
        s.AddSingleton<CaptureOrchestrator>();
        s.AddSingleton<CaptureCoordinator>();
        s.AddSingleton<HotkeyManager>();
        s.AddSingleton<IAppUpdateService, VelopackUpdateService>();
        s.AddSingleton<UpdateCoordinator>();
        s.AddSingleton<UpdateBannerViewModel>();
        s.AddSingleton<ScreenshotActions>();

        s.AddSingleton<IOcrService>(p => new TesseractOcrService(
            new[] { paths.TessdataDirectory, Path.Combine(AppContext.BaseDirectory, "tessdata"), Environment.GetEnvironmentVariable("TESSDATA_PREFIX") ?? "" },
            p.GetRequiredService<ILogger<TesseractOcrService>>()));
        s.AddSingleton<OcrWorkflow>();

        s.AddSingleton<MainViewModel>();
        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<TagsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddTransient<GalleryViewModel>();
        s.AddTransient<DetailsViewModel>();
        s.AddTransient<OcrPanelViewModel>();
        s.AddTransient<RedactionViewModel>();
        s.AddTransient<EditorViewModel>();
        return s;
    }
}
