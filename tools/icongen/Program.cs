using SkiaSharp;

// Generates the Snaply app icon (PNG set + multi-size ICO) from one vector description.
// Design: a rounded-square indigo→violet tile, white viewfinder brackets and a lens with a coral "annotation" dot.

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

int[] sizes = { 16, 24, 32, 48, 64, 96, 128, 256, 512 };
var pngs = new Dictionary<int, byte[]>();
foreach (var s in sizes)
{
    var bytes = Render(s);
    pngs[s] = bytes;
    File.WriteAllBytes(Path.Combine(outDir, $"snaply-{s}.png"), bytes);
}
WriteIco(Path.Combine(outDir, "snaply.ico"), new[] { 16, 24, 32, 48, 64, 128, 256 }.Select(s => (s, pngs[s])).ToList());
Console.WriteLine("done");

static byte[] Render(int size)
{
    var small = size <= 32;
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 512f);

    // Tile
    var tile = new SKRoundRect(new SKRect(32, 32, 480, 480), 112, 112);
    if (size >= 128)
    {
        using var shadow = new SKPaint
        {
            IsAntialias = true, Color = new SKColor(20, 16, 80, 90),
            ImageFilter = SKImageFilter.CreateBlur(12, 12),
        };
        c.Save(); c.Translate(0, 10); c.DrawRoundRect(tile, shadow); c.Restore();
    }
    using (var bg = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(new SKPoint(60, 32), new SKPoint(452, 480),
            new[] { new SKColor(0x5B, 0x8D, 0xFF), new SKColor(0x4A, 0x4B, 0xF0), new SKColor(0x7B, 0x3F, 0xE4) },
            new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp),
    })
        c.DrawRoundRect(tile, bg);

    // Soft top-left light
    if (size >= 48)
    {
        using var light = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(new SKPoint(150, 90), 330,
                new[] { new SKColor(255, 255, 255, 70), new SKColor(255, 255, 255, 0) }, null, SKShaderTileMode.Clamp),
        };
        c.DrawRoundRect(tile, light);
    }

    // Viewfinder brackets
    var stroke = small ? 44f : 30f;
    using var bracket = new SKPaint
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = SKColors.White,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };
    const float lo = 116, hi = 396, len = 92, r = 36;
    foreach (var d in new[]
    {
        $"M{lo} {lo + len} V{lo + r} A{r} {r} 0 0 1 {lo + r} {lo} H{lo + len}",
        $"M{hi - len} {lo} H{hi - r} A{r} {r} 0 0 1 {hi} {lo + r} V{lo + len}",
        $"M{hi} {hi - len} V{hi - r} A{r} {r} 0 0 1 {hi - r} {hi} H{hi - len}",
        $"M{lo + len} {hi} H{lo + r} A{r} {r} 0 0 1 {lo} {hi - r} V{hi - len}",
    })
    {
        using var p = SKPath.ParseSvgPathData(d);
        c.DrawPath(p, bracket);
    }

    // Lens
    using var white = new SKPaint { IsAntialias = true, Color = SKColors.White };
    var lensR = small ? 70f : 64f;
    c.DrawCircle(256, 256, lensR, white);
    using (var inner = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(new SKPoint(210, 210), new SKPoint(300, 304),
            new[] { new SKColor(0x4F, 0x6B, 0xFF), new SKColor(0x6A, 0x3F, 0xE0) }, null, SKShaderTileMode.Clamp),
    })
        c.DrawCircle(256, 256, small ? 38 : 38, inner);

    if (!small)
    {
        using var glint = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 235) };
        c.DrawCircle(243, 243, 9, glint);

        // Coral annotation dot on the upper-right bracket corner
        using var coral = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(360, 70), new SKPoint(420, 130),
                new[] { new SKColor(0xFF, 0x8A, 0x6B), new SKColor(0xFF, 0x4F, 0x7B) }, null, SKShaderTileMode.Clamp),
        };
        using var ring = new SKPaint { IsAntialias = true, Color = new SKColor(0x4A, 0x4B, 0xF0) };
        c.DrawCircle(396, 116, 31, ring);
        c.DrawCircle(396, 116, 22, coral);
    }

    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static void WriteIco(string path, List<(int Size, byte[] Png)> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(png.Length); w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
}
