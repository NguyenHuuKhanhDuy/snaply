using System.Text.Json;
using System.Text.Json.Serialization;
using Snaply.Editor.Model;

namespace Snaply.Editor.Serialization;

/// <summary>Explicit, versioned wire format. Runtime annotation types are never serialized directly.</summary>
public sealed class EditorDocumentDtoV1
{
    public int Version { get; set; } = 1;
    public string? Source { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public RectDto? Crop { get; set; }
    public float ExportScale { get; set; } = 1f;
    public List<AnnotationDtoV1> Annotations { get; set; } = new();
}

public sealed class RectDto
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
}

public sealed class AnnotationDtoV1
{
    public string Type { get; set; } = "";
    public Guid Id { get; set; }
    public RectDto? Rect { get; set; }
    public float X1 { get; set; }
    public float Y1 { get; set; }
    public float X2 { get; set; }
    public float Y2 { get; set; }
    public uint Color { get; set; }
    public float Stroke { get; set; }
    public string? Text { get; set; }
    public float FontSize { get; set; }
    public int Number { get; set; }
    public float Size { get; set; }
    /// <summary>Arrow head: 0 end, 1 none, 2 both. Absent in older documents (= end).</summary>
    public int Head { get; set; }
    /// <summary>Older documents only have this flag (= Dashed).</summary>
    public bool Dashed { get; set; }
    /// <summary>Stroke pattern (see StrokePattern); 0 = solid or not stored.</summary>
    public int Pattern { get; set; }
    public bool Bold { get; set; }
    /// <summary>Text background: 0 none, 1 soft, 2 solid, 3 outline. Absent in older documents (= none).</summary>
    public int Background { get; set; }
    /// <summary>Pen stroke samples as x, y pairs.</summary>
    public List<float>? Points { get; set; }
}

public enum LoadStatus { Ok, NewerVersion, Corrupt }

public sealed record DocumentLoadResult(LoadStatus Status, EditorDocument? Document, string? Message = null);

public static class DocumentSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(EditorDocument doc)
    {
        var dto = new EditorDocumentDtoV1
        {
            Version = doc.Version,
            Source = doc.SourceImagePath,
            Width = doc.Width,
            Height = doc.Height,
            Crop = doc.Crop is { } c ? ToDto(c) : null,
            ExportScale = doc.ExportScale,
            Annotations = doc.Annotations.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static DocumentLoadResult Deserialize(string json)
    {
        try
        {
            using var probe = JsonDocument.Parse(json);
            if (probe.RootElement.ValueKind != JsonValueKind.Object || !probe.RootElement.TryGetProperty("Version", out var v) || !v.TryGetInt32(out var version))
                return new DocumentLoadResult(LoadStatus.Corrupt, null, "Edit data is not recognized.");
            if (version > EditorDocument.CurrentVersion)
                return new DocumentLoadResult(LoadStatus.NewerVersion, null, "These edits were saved by a newer version of Snaply and cannot be loaded.");
            if (version < 1)
                return new DocumentLoadResult(LoadStatus.Corrupt, null, "Edit data is not recognized.");

            var dto = JsonSerializer.Deserialize<EditorDocumentDtoV1>(json, Options);
            if (dto is null || dto.Width <= 0 || dto.Height <= 0)
                return new DocumentLoadResult(LoadStatus.Corrupt, null, "Edit data is incomplete.");

            var doc = new EditorDocument(dto.Source, dto.Width, dto.Height);
            if (dto.Crop is { } crop) doc.Crop = FromDto(crop);
            doc.ExportScale = Math.Clamp(dto.ExportScale <= 0 ? 1f : dto.ExportScale, EditorDocument.MinScale, EditorDocument.MaxScale);
            foreach (var a in dto.Annotations)
            {
                var ann = FromDto(a);
                if (ann is not null) doc.Annotations.Add(ann);
            }
            return new DocumentLoadResult(LoadStatus.Ok, doc);
        }
        catch (JsonException)
        {
            return new DocumentLoadResult(LoadStatus.Corrupt, null, "Edit data is damaged.");
        }
    }

    private static RectDto ToDto(DocRect r) => new() { X = r.X, Y = r.Y, W = r.Width, H = r.Height };
    private static DocRect FromDto(RectDto r) => new(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));

    private static AnnotationDtoV1 ToDto(Annotation a)
    {
        var d = new AnnotationDtoV1 { Type = a.Kind, Id = a.Id };
        switch (a)
        {
            case RectangleAnnotation r: d.Rect = ToDto(r.Rect); d.Color = r.Color; d.Stroke = r.StrokeWidth; d.Dashed = r.Dashed; d.Pattern = (int)r.Pattern; break;
            case EllipseAnnotation e: d.Rect = ToDto(e.Rect); d.Color = e.Color; d.Stroke = e.StrokeWidth; d.Dashed = e.Dashed; d.Pattern = (int)e.Pattern; break;
            case ArrowAnnotation ar: d.X1 = ar.Start.X; d.Y1 = ar.Start.Y; d.X2 = ar.End.X; d.Y2 = ar.End.Y; d.Color = ar.Color; d.Stroke = ar.StrokeWidth; d.Head = (int)ar.Head; d.Dashed = ar.Dashed; d.Pattern = (int)ar.Pattern; break;
            case PenAnnotation pen: d.Color = pen.Color; d.Stroke = pen.StrokeWidth; d.Dashed = pen.Dashed; d.Pattern = (int)pen.Pattern; d.Points = pen.Points.SelectMany(p => new[] { p.X, p.Y }).ToList(); break;
            case SpotlightAnnotation sp: d.Rect = ToDto(sp.Rect); d.Size = sp.Dim; break;
            case HighlightAnnotation h: d.Rect = ToDto(h.Rect); d.Color = h.Color; break;
            case TextAnnotation t: d.X1 = t.Position.X; d.Y1 = t.Position.Y; d.Text = t.Text; d.Color = t.Color; d.FontSize = t.FontSize; d.Bold = t.Bold; d.Background = (int)t.Background; break;
            case NumberedStepAnnotation n: d.X1 = n.Center.X; d.Y1 = n.Center.Y; d.Number = n.Number; d.Size = n.Diameter; d.Color = n.Color; d.Stroke = n.StrokeWidth; break;
            case RedactAnnotation red: d.Rect = ToDto(red.Rect); d.Color = red.Color; break;
            case BlurAnnotation bl: d.Rect = ToDto(bl.Rect); d.Size = bl.Radius; break;
            case PixelateAnnotation p: d.Rect = ToDto(p.Rect); d.Size = p.BlockSize; break;
            default: throw new NotSupportedException($"Unknown annotation type {a.GetType().Name}");
        }
        return d;
    }

    private static StrokePattern ToPattern(AnnotationDtoV1 d) =>
        d.Pattern != 0 && Enum.IsDefined(typeof(StrokePattern), d.Pattern) ? (StrokePattern)d.Pattern
        : d.Dashed ? StrokePattern.Dashed : StrokePattern.Solid;

    private static DocPoint[] ToPoints(List<float>? flat)
    {
        if (flat is null || flat.Count < 2) return Array.Empty<DocPoint>();
        var pts = new DocPoint[flat.Count / 2];
        for (var i = 0; i < pts.Length; i++) pts[i] = new DocPoint(flat[2 * i], flat[2 * i + 1]);
        return pts;
    }

    /// <summary>Returns null for unknown annotation types so a document with future tools still opens.</summary>
    private static Annotation? FromDto(AnnotationDtoV1 d)
    {
        var rect = d.Rect is null ? default : FromDto(d.Rect);
        var id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id;
        return d.Type switch
        {
            "rectangle" => new RectangleAnnotation { Id = id, Rect = rect, Color = d.Color, StrokeWidth = d.Stroke, Pattern = ToPattern(d) },
            "ellipse" => new EllipseAnnotation { Id = id, Rect = rect, Color = d.Color, StrokeWidth = d.Stroke, Pattern = ToPattern(d) },
            "arrow" => new ArrowAnnotation { Id = id, Start = new(d.X1, d.Y1), End = new(d.X2, d.Y2), Color = d.Color, StrokeWidth = d.Stroke, Head = Enum.IsDefined(typeof(ArrowHead), d.Head) ? (ArrowHead)d.Head : ArrowHead.End, Pattern = ToPattern(d) },
            "pen" => new PenAnnotation { Id = id, Color = d.Color, StrokeWidth = d.Stroke <= 0 ? 4 : d.Stroke, Pattern = ToPattern(d), Points = ToPoints(d.Points) },
            "spotlight" => new SpotlightAnnotation { Id = id, Rect = rect, Dim = Math.Clamp(d.Size <= 0 ? SpotlightAnnotation.DefaultDim : d.Size, SpotlightAnnotation.MinDim, SpotlightAnnotation.MaxDim) },
            "highlight" => new HighlightAnnotation { Id = id, Rect = rect, Color = d.Color },
            "text" => new TextAnnotation { Id = id, Position = new(d.X1, d.Y1), Text = d.Text ?? "", Color = d.Color, FontSize = d.FontSize <= 0 ? 28 : d.FontSize, Bold = d.Bold, Background = Enum.IsDefined(typeof(TextBackgroundStyle), d.Background) ? (TextBackgroundStyle)d.Background : TextBackgroundStyle.None },
            "step" => new NumberedStepAnnotation { Id = id, Center = new(d.X1, d.Y1), Number = d.Number, Diameter = d.Size <= 0 ? 40 : d.Size, Color = d.Color, StrokeWidth = d.Stroke },
            "redact" => new RedactAnnotation { Id = id, Rect = rect, Color = (d.Color == 0 ? Colors.RedactFill : d.Color) | 0xFF000000u },
            "blur" => new BlurAnnotation { Id = id, Rect = rect, Radius = d.Size <= 0 ? 12 : d.Size },
            "pixelate" => new PixelateAnnotation { Id = id, Rect = rect, BlockSize = d.Size <= 0 ? 12 : (int)d.Size },
            _ => null,
        };
    }
}
