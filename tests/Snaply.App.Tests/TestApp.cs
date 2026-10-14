using Avalonia;
using Avalonia.Headless;
using Snaply.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Snaply.App.Tests;

/// <summary>Boots the real application resources (tokens, styles, controls, icons) in a headless Skia environment.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::Snaply.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
