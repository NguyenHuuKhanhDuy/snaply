using Microsoft.Extensions.DependencyInjection;
using Snaply.Core.Interfaces;
using Snaply.Platform.Linux;
using Snaply.Platform.MacOS;
using Snaply.Platform.Windows;

namespace Snaply.Platform;

/// <summary>Hotkey service for platforms where global shortcuts are not implemented; explains why instead of failing silently.</summary>
public sealed class UnsupportedHotkeyService : IGlobalHotkeyService
{
    public UnsupportedHotkeyService(string reason) => UnsupportedReason = reason;

    public bool IsSupported => false;
    public string? UnsupportedReason { get; }

    public HotkeyRegistration Register(string id, HotkeyGesture gesture, Action callback) =>
        new(HotkeyStatus.Unsupported, UnsupportedReason);

    public void Unregister(string id) { }
    public void UnregisterAll() { }
    public void Dispose() { }
}

public static class PlatformServices
{
    public static string DescribeOs() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown";

    public static bool IsWaylandSession() =>
        OperatingSystem.IsLinux() &&
        (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
         || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

    public static IServiceCollection AddSnaplyPlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IScreenCaptureService, WindowsCaptureService>();
            services.AddSingleton<IClipboardService, WindowsClipboardService>();
            services.AddSingleton<IGlobalHotkeyService, WindowsHotkeyService>();
            services.AddSingleton<IShellService, WindowsShellService>();
            services.AddSingleton<IStartupRegistration, WindowsStartupRegistration>();
            services.AddSingleton<IActiveWindowService, WindowsActiveWindowService>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IScreenCaptureService, MacCaptureService>();
            services.AddSingleton<IClipboardService, MacClipboardService>();
            services.AddSingleton<IGlobalHotkeyService>(_ => new UnsupportedHotkeyService(
                "Global shortcuts are not implemented on macOS yet. Bind a system keyboard shortcut to run 'snaply --capture-region' instead."));
            services.AddSingleton<IShellService, MacShellService>();
            services.AddSingleton<IStartupRegistration, UnsupportedStartupRegistration>();
            services.AddSingleton<IActiveWindowService, NullActiveWindowService>();
        }
        else
        {
            services.AddSingleton<IScreenCaptureService, LinuxCaptureService>();
            services.AddSingleton<IClipboardService, LinuxClipboardService>();
            services.AddSingleton<IGlobalHotkeyService>(_ => new UnsupportedHotkeyService(IsWaylandSession()
                ? "Wayland does not allow applications to register global shortcuts. Bind a keyboard shortcut in your desktop settings to run 'snaply --capture-region'."
                : "Global shortcuts are not implemented for X11 yet. Bind a keyboard shortcut in your desktop settings to run 'snaply --capture-region'."));
            services.AddSingleton<IShellService, LinuxShellService>();
            services.AddSingleton<IStartupRegistration, UnsupportedStartupRegistration>();
            services.AddSingleton<IActiveWindowService, NullActiveWindowService>();
        }
        return services;
    }
}
