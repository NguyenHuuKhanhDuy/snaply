using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;

namespace Snaply.App.Services;

public sealed class AppWindowController : IAppWindowController
{
    private readonly NavigationHub _hub;
    private bool _wasVisible;

    public AppWindowController(NavigationHub hub) => _hub = hub;

    private static Window? Main => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public Task<bool> HideAsync() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var w = Main;
        _wasVisible = w is { IsVisible: true, WindowState: not WindowState.Minimized };
        if (_wasVisible)
        {
            // Without this Windows fades the window out over ~200 ms, and the fading frame would end up in the capture.
            SetWindowTransitions(w!, enabled: false);
            w!.Hide();
        }
        return _wasVisible;
    }).GetTask();

    private const int DwmwaTransitionsForceDisabled = 3;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void SetWindowTransitions(Window window, bool enabled)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        try
        {
            var disable = enabled ? 0 : 1;
            DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref disable, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* older Windows: keep the default animation */ }
    }

    public Task RestoreAsync() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (!_wasVisible || Main is not { } w) return;
        SetWindowTransitions(w, enabled: true);
        w.Show();
        w.Activate();
        _wasVisible = false;
    }).GetTask();

    public void OpenScreenshotInEditor(long screenshotId) => Dispatcher.UIThread.Post(() =>
    {
        ShowMainWindow();
        _hub.OpenEditor?.Invoke(screenshotId);
    });

    public static void ShowMainWindow()
    {
        if (Main is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }
}

/// <summary>Entry point for every way of starting a capture (buttons, hotkeys, CLI) so they behave identically.</summary>
public sealed class CaptureCoordinator
{
    private readonly CaptureOrchestrator _orchestrator;
    private readonly LibraryEvents _events;

    public CaptureCoordinator(CaptureOrchestrator orchestrator, LibraryEvents events)
    {
        _orchestrator = orchestrator; _events = events;
    }

    public bool CanRepeat => _orchestrator.Last is not null;

    public Task CaptureRegionAsync(int? delay = null) => RunAsync(() => _orchestrator.CaptureAsync(CaptureMode.Region, delay));
    public Task CaptureFullScreenAsync(int? delay = null) => RunAsync(() => _orchestrator.CaptureAsync(CaptureMode.FullScreen, delay));
    public Task RepeatLastAsync() => RunAsync(() => _orchestrator.RepeatLastAsync());

    private async Task RunAsync(Func<Task<Snaply.Core.Common.Result<CaptureOutcome>>> capture)
    {
        var result = await Task.Run(capture).ConfigureAwait(false);
        if (result.IsSuccess && result.Value.Saved is not null) _events.RaiseChanged();
    }
}
