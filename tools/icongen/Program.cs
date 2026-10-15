using SkiaSharp;

// Generates the Snaply app icons (PNG sets + multi-size ICO) from one vector description per logo.
// Three flat, gradient-free logos on a graphite tile (geometry on a 64 unit grid):
//   A "Frame focus" (default): four crop corners around one focus square  -> snaply-<size>.png, snaply.ico
//   B "Crop S":                two crop corners joined into an S           -> snaply-b-<size>.png
//   C "Selection handle":      a selection frame with its resize handle    -> snaply-c-<size>.png

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

int[] sizes = { 16, 24, 32, 48, 64, 96, 128, 256, 512 };
var logos = new[] { ("", 'A'), ("-b", 'B'), ("-c", 'C') };
var iconPngs = new Dictionary<int, byte[]>();
foreach (var (suffix, kind) in logos)
foreach (var s in sizes)
{
    var bytes = Render(s, kind);
    if (kind == 'A') iconPngs[s] = bytes;
    File.WriteAllBytes(Path.Combine(outDir, $"snaply{suffix}-{s}.png"), bytes);
}
WriteIco(Path.Combine(outDir, "snaply.ico"), new[] { 16, 24, 32, 48, 64, 128, 256 }.Select(s => (s, iconPngs[s])).ToList());
Console.WriteLine("done");

static byte[] Render(int size, char kind)
{
    var tileColor = SKColor.Parse("#23262C");
    var ink = SKColor.Parse("#E8EAF0");
    var highlight = SKColor.Parse("#7195D4");

    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);

    using (var bg = new SKPaint { IsAntialias = true, Color = tileColor })
        c.DrawRoundRect(new SKRect(0, 0, size, size), size * 0.22f, size * 0.22f, bg);

    c.Scale(size / 64f);
    SKPaint Stroke(float w, SKColor color) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, Color = color,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
    };
    using var fill = new SKPaint { IsAntialias = true, Color = highlight };

    switch (kind)
    {
        case 'A':
            using (var p = Stroke(5, ink)) using (var path = SKPath.ParseSvgPathData("M16 26V16h10M38 16h10v10M48 38v10H38M26 48H16V38")) c.DrawPath(path, p);
            c.DrawRoundRect(new SKRect(27, 27, 37, 37), 2.5f, 2.5f, fill);
            break;
        case 'B':
            using (var p = Stroke(6, ink)) using (var path = SKPath.ParseSvgPathData("M45 17H20v15M44 32v15H19")) c.DrawPath(path, p);
            using (var p = Stroke(6, highlight)) c.DrawLine(20, 32, 44, 32, p);
            break;
        default:
            using (var p = Stroke(4.5f, ink)) c.DrawRoundRect(new SKRect(14, 14, 45, 45), 3, 3, p);
            c.DrawRoundRect(new SKRect(37, 37, 51, 51), 3, 3, fill);
            using (var p = Stroke(3.5f, tileColor)) c.DrawRoundRect(new SKRect(37, 37, 51, 51), 3, 3, p);   // knocks the handle out of the frame
            break;
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
