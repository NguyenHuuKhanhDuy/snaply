namespace Snaply.Core.Models;

public class Screenshot
{
    public long Id { get; set; }
    public string FilePath { get; set; } = "";
    public string? ThumbnailPath { get; set; }
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Format { get; set; } = "png";
    public long FileSize { get; set; }
    public bool IsFavorite { get; set; }
    public string? OcrText { get; set; }
    public string? OcrLanguage { get; set; }
    public CaptureMethod CreatedByCaptureMethod { get; set; }

    public List<ScreenshotTag> ScreenshotTags { get; set; } = new();
    public AnnotationDocument? AnnotationDocument { get; set; }
}

public class AnnotationDocument
{
    public long Id { get; set; }
    public long ScreenshotId { get; set; }
    public int DocumentVersion { get; set; }
    public string DocumentData { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public Screenshot? Screenshot { get; set; }
}

public class Tag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<ScreenshotTag> ScreenshotTags { get; set; } = new();
}

public class ScreenshotTag
{
    public long ScreenshotId { get; set; }
    public long TagId { get; set; }
    public Screenshot? Screenshot { get; set; }
    public Tag? Tag { get; set; }
}

public class ApplicationSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
