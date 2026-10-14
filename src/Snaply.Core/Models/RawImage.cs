namespace Snaply.Core.Models;

/// <summary>Uncompressed 32-bit BGRA pixel buffer (captures are always opaque).</summary>
public sealed class RawImage
{
    public RawImage(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image must have positive size.");
        if (bgra.Length != checked(width * height * 4)) throw new ArgumentException("Pixel buffer does not match dimensions.", nameof(bgra));
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride => Width * 4;

    public RawImage Crop(PixelRect area)
    {
        var r = area.Intersect(new PixelRect(0, 0, Width, Height));
        if (r.IsEmpty) throw new ArgumentOutOfRangeException(nameof(area), "Crop area is outside the image.");
        var dst = new byte[r.Width * r.Height * 4];
        for (var row = 0; row < r.Height; row++)
            Buffer.BlockCopy(Bgra, ((r.Y + row) * Width + r.X) * 4, dst, row * r.Width * 4, r.Width * 4);
        return new RawImage(r.Width, r.Height, dst);
    }

    /// <summary>True when every pixel is black, a typical symptom of a blocked capture.</summary>
    public bool IsAllBlack()
    {
        for (var i = 0; i < Bgra.Length; i += 4)
            if (Bgra[i] != 0 || Bgra[i + 1] != 0 || Bgra[i + 2] != 0) return false;
        return true;
    }
}
