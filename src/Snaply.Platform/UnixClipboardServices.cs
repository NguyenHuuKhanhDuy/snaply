using System.Text;
using SkiaSharp;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.Platform;

internal static class PngEncoder
{
    public static byte[] Encode(RawImage image)
    {
        using var bmp = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(image.Bgra, 0, bmp.GetPixels(), image.Bgra.Length);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>macOS clipboard via pbcopy and osascript. IMPLEMENTED BUT UNVERIFIED on macOS.</summary>
public sealed class MacClipboardService : IClipboardService
{
    public async Task<Result> SetTextAsync(string text, CancellationToken ct = default)
    {
        var run = await ProcessRunner.RunAsync("pbcopy", Array.Empty<string>(), Encoding.UTF8.GetBytes(text), ct: ct).ConfigureAwait(false);
        return run.Started && run.ExitCode == 0 ? Result.Ok() : Result.Fail(ErrorCode.Failed, "Could not copy text to the clipboard.");
    }

    public async Task<Result> SetImageAsync(RawImage image, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"snaply-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(tmp, PngEncoder.Encode(image), ct).ConfigureAwait(false);
            var script = $"set the clipboard to (read (POSIX file \"{tmp}\") as «class PNGf»)";
            var run = await ProcessRunner.RunAsync("osascript", new[] { "-e", script }, ct: ct).ConfigureAwait(false);
            return run.Started && run.ExitCode == 0 ? Result.Ok() : Result.Fail(ErrorCode.Failed, "Could not copy the image to the clipboard.");
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }
}

/// <summary>Linux clipboard via wl-copy (Wayland) or xclip / xsel (X11). IMPLEMENTED BUT UNVERIFIED on Linux.</summary>
public sealed class LinuxClipboardService : IClipboardService
{
    private static bool IsWayland =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        || string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    private static Result Missing() => Result.Fail(ErrorCode.Unsupported,
        IsWayland ? "Install wl-clipboard (wl-copy) to copy from Snaply." : "Install xclip to copy from Snaply.");

    public async Task<Result> SetTextAsync(string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (IsWayland && ProcessRunner.IsOnPath("wl-copy")) return From(await ProcessRunner.RunAsync("wl-copy", Array.Empty<string>(), bytes, ct: ct).ConfigureAwait(false));
        if (ProcessRunner.IsOnPath("xclip")) return From(await ProcessRunner.RunAsync("xclip", new[] { "-selection", "clipboard" }, bytes, ct: ct).ConfigureAwait(false));
        return Missing();
    }

    public async Task<Result> SetImageAsync(RawImage image, CancellationToken ct = default)
    {
        var png = PngEncoder.Encode(image);
        if (IsWayland && ProcessRunner.IsOnPath("wl-copy")) return From(await ProcessRunner.RunAsync("wl-copy", new[] { "--type", "image/png" }, png, ct: ct).ConfigureAwait(false));
        if (ProcessRunner.IsOnPath("xclip")) return From(await ProcessRunner.RunAsync("xclip", new[] { "-selection", "clipboard", "-t", "image/png" }, png, ct: ct).ConfigureAwait(false));
        return Missing();
    }

    private static Result From(ProcessRunner.RunResult r) =>
        r.Started && r.ExitCode == 0 ? Result.Ok() : Result.Fail(ErrorCode.Failed, "The clipboard tool failed to copy.");
}
