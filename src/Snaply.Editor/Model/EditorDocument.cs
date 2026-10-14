namespace Snaply.Editor.Model;

/// <summary>
/// Non-destructive editing document: a reference to the original image plus ordered annotations, crop and export scale.
/// Coordinates are in source-image pixels. Mutate only through <see cref="CommandHistory"/> commands.
/// </summary>
public sealed class EditorDocument
{
    public const int CurrentVersion = 1;
    public const float MinScale = 0.1f, MaxScale = 4f;

    public EditorDocument(string? sourceImagePath, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        SourceImagePath = sourceImagePath;
        Width = width;
        Height = height;
    }

    public int Version => CurrentVersion;
    public string? SourceImagePath { get; }
    public int Width { get; }
    public int Height { get; }

    public DocRect? Crop { get; internal set; }
    public float ExportScale { get; internal set; } = 1f;

    /// <summary>Z-order: first item is drawn first (bottom), last item on top.</summary>
    public List<Annotation> Annotations { get; } = new();

    public DocRect Full => new(0, 0, Width, Height);
    public DocRect OutputArea => Crop ?? Full;

    public (int Width, int Height) OutputSize
    {
        get
        {
            var a = OutputArea;
            return (Math.Max(1, (int)Math.Round(a.Width * ExportScale)), Math.Max(1, (int)Math.Round(a.Height * ExportScale)));
        }
    }

    public bool HasEdits => Annotations.Count > 0 || Crop is not null || Math.Abs(ExportScale - 1f) > 0.0001f;

    /// <summary>Sets the crop directly (not undoable); used by the capture overlay while the selection is adjusted.</summary>
    public void SetCrop(DocRect? crop)
    {
        if (crop is not { } c) { Crop = null; return; }
        var r = new DocRect((float)Math.Round(c.X), (float)Math.Round(c.Y), (float)Math.Round(c.Width), (float)Math.Round(c.Height)).Intersect(Full);
        Crop = r.Width < 1 || r.Height < 1 ? null : r;
    }

    public int NextStepNumber() => Annotations.OfType<NumberedStepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
}
