using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Snaply.App.Controls;

namespace Snaply.App.Services;

/// <summary>
/// A small confirmation ("Copied to clipboard") floating at the bottom centre of the screen. It is its own window, so it shows even when
/// Snaply's main window is hidden in the tray (which is the normal case right after a capture). It never takes focus or clicks.
/// </summary>
public static class QuickToast
{
    private static Window? _current;

    /// <summary>The toast window while one is showing (for tests).</summary>
    public static Window? Current => _current;
    private static DispatcherTimer? _timer;

    public static void Show(string text, string? detail = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try { ShowCore(text, detail); }
            catch { /* a failed confirmation must never break the capture that triggered it */ }
        });
    }

    private static void ShowCore(string text, string? detail)
    {
        Close();

        var app = Application.Current;
        object? Res(string key) => app is not null && app.TryFindResource(key, app.ActualThemeVariant, out var v) ? v : null;

        var check = new Icon { Data = Res("IconCheck") as Geometry, Size = 16, Stroke = Res("SuccessBrush") as IBrush };
        var title = new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextPrimaryBrush") as IBrush };
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { title } };
        if (!string.IsNullOrWhiteSpace(detail))
            texts.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Res("TextSecondaryBrush") as IBrush });

        var badge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Center,
            Background = Res("SuccessSoftBrush") as IBrush, Child = check,
        };
        check.HorizontalAlignment = HorizontalAlignment.Center; check.VerticalAlignment = VerticalAlignment.Center;

        var card = new Border
        {
            Background = Res("SurfaceBrush") as IBrush, BorderBrush = Res("BorderStrongBrush") as IBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 18, 9), Margin = new Thickness(14),
            BoxShadow = BoxShadows.Parse("0 6 18 0 #40000000"),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { badge, texts } },
        };

        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, CanResize = false, Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = card,
            WindowStartupLocation = WindowStartupLocation.Manual,
            IsHitTestVisible = false,
        };
        if (app is not null) window.RequestedThemeVariant = app.ActualThemeVariant;

        window.Measure(Size.Infinity);
        var size = window.DesiredSize;
        var screen = ScreenUnderCursor(window);
        var scale = screen?.Scaling ?? 1.0;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var x = area.X + (int)((area.Width - size.Width * scale) / 2);
            var y = area.Bottom - (int)((size.Height + 40) * scale);
            window.Position = new PixelPoint(x, y);
        }

        window.Opened += (_, _) => MakeClickThrough(window);
        _current = window;
        window.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    private static void Close()
    {
        _timer?.Stop(); _timer = null;
        var w = _current; _current = null;
        try { w?.Close(); } catch { /* already closed */ }
    }

    private static Avalonia.Platform.Screen? ScreenUnderCursor(Window window)
    {
        var screens = window.Screens;
        if (OperatingSystem.IsWindows() && GetCursorPos(out var p)) return screens.ScreenFromPoint(new PixelPoint(p.X, p.Y)) ?? screens.Primary;
        return screens.Primary;
    }

    // ---- Windows: never take focus or mouse clicks, and stay out of Alt+Tab ----
    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;

    private static void MakeClickThrough(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
}
