using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Snaply.App.Services;
using Snaply.App.ViewModels;
using Snaply.App.Views;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;
using Snaply.Infrastructure;
using Snaply.Infrastructure.Persistence;
using Snaply.Ocr;
using Snaply.Platform;

namespace Snaply.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Microsoft.Extensions.Logging.ILogger? _log;

    public static IServiceProvider Services => ((App)Current!)._services!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                _services = BuildServices();
                _log = _services.GetRequiredService<ILoggerFactory>().CreateLogger("Snaply");
                InitializeData(_services);
            }
            catch (Exception ex) when (ex is SchemaTooNewException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                // Without a working data store there is nothing useful to show; explain and exit.
                desktop.MainWindow = new FatalErrorWindow("Snaply can't start", ex is SchemaTooNewException ? ex.Message : $"Snaply could not open its data folder or database.\n\n{ex.Message}");
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var theme = _services.GetRequiredService<ThemeService>();
            var settings = _services.GetRequiredService<IApplicationSettingsService>();
            theme.Apply(settings.Current.Theme);
            theme.ApplyMotion(settings.Current.ReduceMotion);

            var main = _services.GetRequiredService<MainViewModel>();
            var window = new MainWindow { DataContext = main };
            desktop.MainWindow = window;
            SetupBackgroundMode(desktop, window);
            window.Opened += async (_, _) => await OnWindowOpenedAsync(main);
            desktop.ShutdownRequested += (_, _) => Shutdown();
            desktop.Exit += (_, _) => (_services as IDisposable)?.Dispose();

            RegisterGlobalHandlers(_services.GetRequiredService<NotificationService>());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private bool _quitting;
    private bool _trayAvailable;
    private bool _toldAboutTray;
    private TrayIcon? _tray;

    /// <summary>
    /// Closing the window hides it to the system tray (when enabled) so global shortcuts keep working. The tray menu's Quit really exits.
    /// </summary>
    private void SetupBackgroundMode(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = _services!.GetRequiredService<IApplicationSettingsService>();
        var notes = _services!.GetRequiredService<INotificationService>();

        try
        {
            using var icon = AssetLoader.Open(new Uri("avares://Snaply/Assets/snaply-32.png"));
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open Snaply");
            open.Click += (_, _) => Dispatcher.UIThread.Post(AppWindowController.ShowMainWindow);
            var region = new NativeMenuItem("Capture region");
            region.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = RunCommandAsync(StartupCommand.CaptureRegion));
            var full = new NativeMenuItem("Capture full screen");
            full.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = RunCommandAsync(StartupCommand.CaptureFullScreen));
            var quit = new NativeMenuItem("Quit Snaply");
            quit.Click += (_, _) => Dispatcher.UIThread.Post(() => Quit(desktop));
            menu.Add(open); menu.Add(new NativeMenuItemSeparator()); menu.Add(region); menu.Add(full); menu.Add(new NativeMenuItemSeparator()); menu.Add(quit);

            _tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = "Snaply", Menu = menu, IsVisible = true };
            _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(AppWindowController.ShowMainWindow);
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            _trayAvailable = true;
        }
        catch (Exception ex)
        {
            // No tray on this desktop: fall back to the normal behaviour of quitting when the window closes.
            _log?.LogWarning(ex, "System tray is not available");
            _trayAvailable = false;
        }

        window.Closing += (_, e) =>
        {
            if (_quitting || !_trayAvailable || !settings.Current.RunInBackground || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown) return;
            e.Cancel = true;
            window.Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                notes.Show(NotificationKind.Info, "Snaply is still running", "It stays in the system tray so your shortcuts keep working. Right-click the tray icon and choose Quit to exit.");
            }
        };
        window.Closed += (_, _) => { if (!_quitting) Quit(desktop); };
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_quitting) return;
        _quitting = true;
        _tray?.Dispose();
        desktop.Shutdown();
    }

    private static ServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables("SNAPLY_")
            .Build();

        var configured = config["Snaply:DataDirectory"];
        if (string.IsNullOrWhiteSpace(configured)) configured = Environment.GetEnvironmentVariable("SNAPLY_DATA_DIR");
        var paths = new AppPaths(string.IsNullOrWhiteSpace(configured) ? AppPaths.DefaultDataDirectory() : configured);
        paths.EnsureCreated();

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "snaply-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var s = new ServiceCollection();
        s.AddSingleton<IConfiguration>(config);
        s.AddLogging(b => b.ClearProviders().AddSerilog(logger, dispose: true));
        s.AddSnaplyInfrastructure(paths);
        s.AddSnaplyPlatform();

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
        s.AddTransient<EditorViewModel>();

        return s.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }

    private static void InitializeData(IServiceProvider services)
    {
        // Startup is synchronous by design: the UI needs persisted settings (theme) before the first window appears.
        Task.Run(async () =>
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync();
            await services.GetRequiredService<IApplicationSettingsService>().LoadAsync();
        }).GetAwaiter().GetResult();
    }

    private async Task OnWindowOpenedAsync(MainViewModel main)
    {
        await main.InitializeAsync();
        StartHotkeys();
        StartUpdateChecks(main);
        Program.Instance?.Listen(cmd => Dispatcher.UIThread.Post(() => _ = RunCommandAsync(cmd)));
        if (Program.InitialCommand != StartupCommand.Open) await RunCommandAsync(Program.InitialCommand);
    }

    private void StartUpdateChecks(MainViewModel main)
    {
        var coordinator = _services!.GetRequiredService<UpdateCoordinator>();
        var capture = _services!.GetRequiredService<CaptureOrchestrator>();
        coordinator.IsCaptureActive = () => capture.IsBusy;
        main.Updates.Refresh();

        // Check a few seconds after startup, then re-check periodically so an app left running in the tray still learns about new
        // versions. The coordinator applies the setting and the 1-hour cooldown, and failures stay silent.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await coordinator.CheckAsync(manual: false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
            while (await timer.WaitForNextTickAsync()) await coordinator.CheckAsync(manual: false);
        });
    }

    private void StartHotkeys()
    {
        var hotkeys = _services!.GetRequiredService<HotkeyManager>();
        hotkeys.SetHandler(HotkeyActions.CaptureRegion, () => Dispatcher.UIThread.Post(() => _ = RunCommandAsync(StartupCommand.CaptureRegion)));
        hotkeys.SetHandler(HotkeyActions.CaptureFullScreen, () => Dispatcher.UIThread.Post(() => _ = RunCommandAsync(StartupCommand.CaptureFullScreen)));
        hotkeys.SetHandler(HotkeyActions.OpenApp, () => Dispatcher.UIThread.Post(AppWindowController.ShowMainWindow));
        hotkeys.ApplyAll();

        var notes = _services!.GetRequiredService<INotificationService>();
        var failed = hotkeys.Status.Where(kv => kv.Value.Status is HotkeyStatus.Conflict or HotkeyStatus.Failed).ToList();
        if (failed.Count > 0)
            notes.Show(NotificationKind.Warning, "Some shortcuts are unavailable", "Open Settings → Keyboard Shortcuts to see why and choose different ones.");
    }

    private async Task RunCommandAsync(StartupCommand cmd)
    {
        var capture = _services!.GetRequiredService<CaptureCoordinator>();
        switch (cmd)
        {
            case StartupCommand.CaptureRegion: await capture.CaptureRegionAsync(); break;
            case StartupCommand.CaptureFullScreen: await capture.CaptureFullScreenAsync(); break;
            default: AppWindowController.ShowMainWindow(); break;
        }
    }

    private void Shutdown()
    {
        try { _services?.GetService<HotkeyManager>()?.Dispose(); }
        catch (Exception ex) { _log?.LogWarning(ex, "Releasing hotkeys on exit failed"); }
    }

    private void RegisterGlobalHandlers(NotificationService notes)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unhandled UI exception");
            notes.Show(NotificationKind.Error, "Something went wrong", "The action could not be completed. Details were written to the log.");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _log?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Term})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }
}
