using SkiaSharp;
using Snaply.Core.Common;
using Snaply.Core.Models;

namespace Snaply.Platform;

internal static class PngDecoder
{
    private const int MaxDimension = 32_000;

    public static Result<RawImage> Decode(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return Result<RawImage>.Fail(ErrorCode.Failed, "The capture tool produced an unreadable image.");
        var i = codec.Info;
        if (i.Width <= 0 || i.Height <= 0 || i.Width > MaxDimension || i.Height > MaxDimension)
            return Result<RawImage>.Fail(ErrorCode.Failed, "The captured image has an unsupported size.");

        using var bmp = SKBitmap.Decode(codec, new SKImageInfo(i.Width, i.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        if (bmp is null) return Result<RawImage>.Fail(ErrorCode.Failed, "The captured image could not be decoded.");
        return Result<RawImage>.Ok(new RawImage(bmp.Width, bmp.Height, bmp.Bytes));
    }
}
