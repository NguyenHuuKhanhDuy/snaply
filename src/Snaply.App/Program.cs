using Avalonia;
using Snaply.App.Services;
using Velopack;

namespace Snaply.App;

internal static class Program
{
    public static SingleInstance? Instance { get; private set; }
    public static StartupCommand InitialCommand { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks and exits quickly when Velopack launched us for one.
        VelopackApp.Build().Run();

        InitialCommand = StartupCommands.Parse(args);

        // Tests and portable runs can use a separate scope so they never collide with the user's instance.
        Instance = SingleInstance.Acquire(Environment.GetEnvironmentVariable("SNAPLY_INSTANCE_SCOPE"));
        if (!Instance.IsFirst)
        {
            Instance.SendToRunning(InitialCommand);
            Instance.Dispose();
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
