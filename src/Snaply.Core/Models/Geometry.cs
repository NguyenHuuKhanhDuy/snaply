namespace Snaply.Core.Models;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromCorners(int x1, int y1, int x2, int y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public PixelRect Intersect(PixelRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        var r = Math.Min(Right, other.Right);
        var b = Math.Min(Bottom, other.Bottom);
        return r <= x || b <= y ? new PixelRect(x, y, 0, 0) : new PixelRect(x, y, r - x, b - y);
    }

    public bool Contains(int px, int py) => px >= X && py >= Y && px < Right && py < Bottom;

    public static PixelRect Union(IEnumerable<PixelRect> rects)
    {
        var any = false;
        int l = 0, t = 0, r = 0, b = 0;
        foreach (var rc in rects)
        {
            if (!any) { l = rc.X; t = rc.Y; r = rc.Right; b = rc.Bottom; any = true; continue; }
            l = Math.Min(l, rc.X); t = Math.Min(t, rc.Y); r = Math.Max(r, rc.Right); b = Math.Max(b, rc.Bottom);
        }
        return any ? new PixelRect(l, t, r - l, b - t) : default;
    }
}

public sealed record MonitorInfo(int Index, PixelRect Bounds, double Scale, bool IsPrimary);
