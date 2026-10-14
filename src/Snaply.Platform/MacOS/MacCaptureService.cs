using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Platform.MacOS;

/// <summary>
/// macOS capture through the system <c>screencapture</c> tool. Monitor bounds are reported in points (the unit screencapture
/// expects); returned images may be larger (Retina) and the core scales selections accordingly.
/// IMPLEMENTED BUT UNVERIFIED: this code has not been run on macOS.
/// </summary>
public sealed class MacCaptureService : IScreenCaptureService
{
    private readonly ILogger<MacCaptureService> _log;
    private bool _requestedOnce;

    public MacCaptureService(ILogger<MacCaptureService> log) => _log = log;

    public CaptureSupport Support => new(true, Note: "Requires the Screen Recording permission.");

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize { public double Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public CGPoint Origin; public CGSize Size; }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] private static extern int CGGetActiveDisplayList(uint max, [Out] uint[] displays, out uint count);
    [DllImport(CoreGraphics)] private static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphics)] private static extern nuint CGDisplayPixelsWide(uint display);
    [DllImport(CoreGraphics)] private static extern uint CGMainDisplayID();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            var ids = new uint[16];
            if (CGGetActiveDisplayList((uint)ids.Length, ids, out var count) != 0) return list;
            var main = CGMainDisplayID();
            for (var i = 0; i < count; i++)
            {
                var b = CGDisplayBounds(ids[i]);
                var scale = b.Size.Width > 0 ? (double)CGDisplayPixelsWide(ids[i]) / b.Size.Width : 1;
                list.Add(new MonitorInfo(i, new PixelRect((int)b.Origin.X, (int)b.Origin.Y, (int)b.Size.Width, (int)b.Size.Height), scale, ids[i] == main));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.LogError(ex, "CoreGraphics is not available");
        }
        return list;
    }

    public PixelPoint? GetCursorPosition() => null; // falls back to the primary display

    public async Task<Result<RawImage>> CaptureAsync(PixelRect area, CancellationToken ct = default)
    {
        bool allowed;
        try { allowed = CGPreflightScreenCaptureAccess(); }
        catch (EntryPointNotFoundException) { allowed = true; } // macOS < 11 has no preflight; the capture itself will tell
        if (!allowed)
        {
            if (!_requestedOnce)
            {
                _requestedOnce = true;
                try { CGRequestScreenCaptureAccess(); } catch (EntryPointNotFoundException) { }
            }
            return Result<RawImage>.Fail(ErrorCode.PermissionDenied,
                "Snaply needs the Screen Recording permission. Open System Settings > Privacy & Security > Screen Recording, enable Snaply, then restart the app.");
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"snaply-{Guid.NewGuid():N}.png");
        try
        {
            var run = await ProcessRunner.RunAsync("/usr/sbin/screencapture",
                new[] { "-x", "-t", "png", "-R", $"{area.X},{area.Y},{area.Width},{area.Height}", tmp }, ct: ct).ConfigureAwait(false);
            if (!run.Started || run.ExitCode != 0 || !File.Exists(tmp))
            {
                _log.LogWarning("screencapture failed: exit {Exit} {Err}", run.ExitCode, run.StdErr);
                return Result<RawImage>.Fail(ErrorCode.Failed, "macOS could not capture the screen. Check the Screen Recording permission.");
            }
            return PngDecoder.Decode(tmp);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }
}
