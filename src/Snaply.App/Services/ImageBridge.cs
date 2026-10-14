using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Snaply.Core.Models;

namespace Snaply.App.Services;

/// <summary>Conversions between Core/Skia pixel buffers and Avalonia bitmaps.</summary>
public static class ImageBridge
{
    public static WriteableBitmap ToBitmap(RawImage image) => ToBitmap(image.Bgra, image.Width, image.Height, AlphaFormat.Opaque);

    public static WriteableBitmap ToBitmap(SKBitmap bitmap)
    {
        var alpha = bitmap.AlphaType == SKAlphaType.Opaque ? AlphaFormat.Opaque : AlphaFormat.Premul;
        return ToBitmap(bitmap.Bytes, bitmap.Width, bitmap.Height, alpha, bitmap.RowBytes);
    }

    private static WriteableBitmap ToBitmap(byte[] bgra, int width, int height, AlphaFormat alpha, int? srcStride = null)
    {
        var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, alpha);
        using var fb = wb.Lock();
        var stride = srcStride ?? width * 4;
        if (fb.RowBytes == stride)
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, stride * height));
        else
            for (var y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * stride, fb.Address + y * fb.RowBytes, width * 4);
        return wb;
    }

    public static RawImage ToRaw(SKBitmap bitmap)
    {
        using var opaque = bitmap.AlphaType == SKAlphaType.Opaque && bitmap.ColorType == SKColorType.Bgra8888 ? null : Flatten(bitmap);
        var src = opaque ?? bitmap;
        var bytes = src.RowBytes == src.Width * 4 ? src.Bytes : CopyTight(src);
        return new RawImage(src.Width, src.Height, bytes);
    }

    private static SKBitmap Flatten(SKBitmap bitmap)
    {
        var flat = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(flat);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(bitmap, 0, 0);
        return flat;
    }

    private static byte[] CopyTight(SKBitmap b)
    {
        var all = b.Bytes;
        var tight = new byte[b.Width * b.Height * 4];
        for (var y = 0; y < b.Height; y++) Buffer.BlockCopy(all, y * b.RowBytes, tight, y * b.Width * 4, b.Width * 4);
        return tight;
    }

    /// <summary>Loads an image file at reduced size for display. Returns null when the file cannot be decoded.</summary>
    public static Bitmap? LoadScaled(string path, int maxWidth)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Bitmap.DecodeToWidth(fs, maxWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
