using SkiaSharp;
using Snaply.Core.Models;

namespace Snaply.Infrastructure.Storage;

/// <summary>Skia-based encode/decode helpers. Decoding treats files as untrusted and enforces size limits.</summary>
public static class ImageCodec
{
    public const int MaxDimension = 32_000;
    public const long MaxPixels = 200_000_000;

    public static string Extension(ImageFormat f) => f == ImageFormat.Jpeg ? "jpg" : "png";

    public static ImageFormat? FormatFromExtension(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => ImageFormat.Png,
        "jpg" or "jpeg" => ImageFormat.Jpeg,
        _ => null,
    };

    public static SKBitmap ToBitmap(RawImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Bgra, 0, bmp.GetPixels(), image.Bgra.Length);
        return bmp;
    }

    public static byte[] Encode(SKBitmap bitmap, ImageFormat format, int quality = 92)
    {
        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(format == ImageFormat.Jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, quality);
        if (data is null) throw new InvalidOperationException("The image could not be encoded.");
        return data.ToArray();
    }

    public static byte[] Encode(RawImage image, ImageFormat format, int quality = 92)
    {
        using var bmp = ToBitmap(image);
        return Encode(bmp, format, quality);
    }

    public static (int Width, int Height)? ReadSize(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>Decodes an image file after validating its declared dimensions. Returns null when unreadable or unreasonably large.</summary>
    public static SKBitmap? DecodeSafe(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var i = codec.Info;
        if (i.Width <= 0 || i.Height <= 0 || i.Width > MaxDimension || i.Height > MaxDimension || (long)i.Width * i.Height > MaxPixels) return null;
        return SKBitmap.Decode(codec, new SKImageInfo(i.Width, i.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
    }
}
