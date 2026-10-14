using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Platform.Windows;

/// <summary>
/// GDI-based capture of the virtual desktop. Requires the process to be per-monitor DPI aware (declared in the app manifest)
/// so that all coordinates are physical pixels.
/// </summary>
public sealed class WindowsCaptureService : IScreenCaptureService
{
    private readonly ILogger<WindowsCaptureService> _log;

    public WindowsCaptureService(ILogger<WindowsCaptureService> log) => _log = log;

    public CaptureSupport Support { get; } = new(true);

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(hMon, ref info)) return true;
            var r = info.rcMonitor;
            double scale = 1;
            try
            {
                if (Native.GetDpiForMonitor(hMon, Native.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
            }
            catch (DllNotFoundException) { /* Windows 7: fall back to 100% */ }
            list.Add(new MonitorInfo(list.Count, new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), scale, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            // Extremely unusual, but fall back to the system metrics rather than reporting no screens.
            list.Add(new MonitorInfo(0, new PixelRect(Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN)), 1, true));
        }
        return list;
    }

    public PixelPoint? GetCursorPosition() => Native.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : null;

    public Task<Result<RawImage>> CaptureAsync(PixelRect area, CancellationToken ct = default) =>
        Task.Run(() => Capture(area), ct);

    private Result<RawImage> Capture(PixelRect area)
    {
        if (area.IsEmpty) return Result<RawImage>.Fail(ErrorCode.InvalidInput, "The capture area is empty.");

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = Native.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return Result<RawImage>.Fail(ErrorCode.Failed, "Windows did not give access to the screen.");

            memDc = Native.CreateCompatibleDC(screenDc);
            bmp = Native.CreateCompatibleBitmap(screenDc, area.Width, area.Height);
            if (memDc == IntPtr.Zero || bmp == IntPtr.Zero) return Result<RawImage>.Fail(ErrorCode.Failed, "Not enough graphics memory to capture the screen.");

            old = Native.SelectObject(memDc, bmp);
            if (!Native.BitBlt(memDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                return Result<RawImage>.Fail(ErrorCode.Failed, "Windows refused to copy the screen contents (the display may be locked or protected).");

            var header = new Native.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(), biWidth = area.Width, biHeight = -area.Height, // negative = top-down
                biPlanes = 1, biBitCount = 32, biCompression = Native.BI_RGB,
            };
            var pixels = new byte[checked(area.Width * area.Height * 4)];
            // The bitmap must not be selected into a DC while calling GetDIBits.
            Native.SelectObject(memDc, old); old = IntPtr.Zero;
            var lines = Native.GetDIBits(memDc, bmp, 0, (uint)area.Height, pixels, ref header, Native.DIB_RGB_COLORS);
            if (lines != area.Height) return Result<RawImage>.Fail(ErrorCode.Failed, "The captured pixels could not be read.");

            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // GDI leaves alpha undefined

            var image = new RawImage(area.Width, area.Height, pixels);
            if (image.IsAllBlack())
                _log.LogWarning("Captured image is entirely black; protected content or a locked session is likely");
            return Result<RawImage>.Ok(image);
        }
        catch (Exception ex) when (ex is OverflowException or OutOfMemoryException)
        {
            return Result<RawImage>.Fail(ErrorCode.Failed, "The selected area is too large to capture.");
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
