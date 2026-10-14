using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Platform.Linux;

/// <summary>
/// Linux capture through whichever screenshot tool is installed. Captures the full desktop once and crops, so it works
/// for any tool that can write a PNG. IMPLEMENTED BUT UNVERIFIED: this code has not been run on Linux.
/// X11: maim, scrot, import (ImageMagick). Wayland: grim (wlroots compositors), gnome-screenshot, spectacle.
/// The XDG desktop portal is not used yet, so some Wayland compositors are unsupported.
/// </summary>
public sealed class LinuxCaptureService : IScreenCaptureService
{
    private static readonly (string Tool, string[] Args)[] X11Tools =
    {
        ("maim", new[] { "{out}" }),
        ("scrot", new[] { "-o", "{out}" }),
        ("import", new[] { "-window", "root", "{out}" }),
    };

    private static readonly (string Tool, string[] Args)[] WaylandTools =
    {
        ("grim", new[] { "{out}" }),
        ("gnome-screenshot", new[] { "-f", "{out}" }),
        ("spectacle", new[] { "-b", "-n", "-f", "-o", "{out}" }),
    };

    private readonly ILogger<LinuxCaptureService> _log;
    private readonly bool _wayland;
    private readonly (string Tool, string[] Args)? _tool;

    public LinuxCaptureService(ILogger<LinuxCaptureService> log)
    {
        _log = log;
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        _wayland = string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        var candidates = _wayland ? WaylandTools : X11Tools;
        foreach (var c in candidates)
            if (ProcessRunner.IsOnPath(c.Tool)) { _tool = c; break; }

        Support = _tool is not null
            ? new CaptureSupport(true, Note: $"Using '{_tool.Value.Tool}' on {(_wayland ? "Wayland" : "X11")}.")
            : new CaptureSupport(false, $"No screenshot tool found for {(_wayland ? "Wayland" : "X11")}. Install one of: {string.Join(", ", candidates.Select(c => c.Tool))}.");
    }

    public CaptureSupport Support { get; }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        if (!_wayland && ProcessRunner.IsOnPath("xrandr"))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("xrandr", "--query") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                var output = p!.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                var list = new List<MonitorInfo>();
                foreach (Match m in Regex.Matches(output, @"^\S+ connected( primary)? (\d+)x(\d+)\+(\d+)\+(\d+)", RegexOptions.Multiline))
                    list.Add(new MonitorInfo(list.Count, new PixelRect(int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)), 1, m.Groups[1].Success));
                if (list.Count > 0) return list;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FormatException)
            {
                _log.LogWarning(ex, "xrandr query failed");
            }
        }

        // Unknown layout: capture once to learn the desktop size and treat it as a single monitor.
        var probe = CaptureDesktopAsync(CancellationToken.None).GetAwaiter().GetResult();
        return probe.IsSuccess
            ? new[] { new MonitorInfo(0, new PixelRect(0, 0, probe.Value.Width, probe.Value.Height), 1, true) }
            : Array.Empty<MonitorInfo>();
    }

    public PixelPoint? GetCursorPosition() => null;

    private async Task<Result<RawImage>> CaptureDesktopAsync(CancellationToken ct)
    {
        if (_tool is not { } tool) return Result<RawImage>.Fail(ErrorCode.Unsupported, Support.UnsupportedReason ?? "No screenshot tool available.");
        var tmp = Path.Combine(Path.GetTempPath(), $"snaply-{Guid.NewGuid():N}.png");
        try
        {
            var run = await ProcessRunner.RunAsync(tool.Tool, tool.Args.Select(a => a.Replace("{out}", tmp)), ct: ct).ConfigureAwait(false);
            if (!run.Started || run.ExitCode != 0 || !File.Exists(tmp))
            {
                _log.LogWarning("{Tool} failed: exit {Exit} {Err}", tool.Tool, run.ExitCode, run.StdErr);
                return Result<RawImage>.Fail(ErrorCode.Failed, $"'{tool.Tool}' could not capture the screen. Your desktop may not allow it.");
            }
            return PngDecoder.Decode(tmp);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    public async Task<Result<RawImage>> CaptureAsync(PixelRect area, CancellationToken ct = default)
    {
        var all = await CaptureDesktopAsync(ct).ConfigureAwait(false);
        if (!all.IsSuccess) return all;
        var desktop = new PixelRect(0, 0, all.Value.Width, all.Value.Height);
        var r = area.Intersect(desktop);
        if (r.IsEmpty) return Result<RawImage>.Fail(ErrorCode.InvalidInput, "The capture area is outside the screen.");
        return r == desktop ? all : Result<RawImage>.Ok(all.Value.Crop(r));
    }
}
