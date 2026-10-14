namespace Snaply.Core.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string? SaveDirectory { get; set; }
    public ImageFormat DefaultFormat { get; set; } = ImageFormat.Png;
    public bool CopyToClipboardAfterCapture { get; set; }
    /// <summary>Auto-save: store every capture in the library as soon as it is finished.</summary>
    public bool SaveToLibraryAfterCapture { get; set; } = true;
    /// <summary>After choosing a region, annotate it in place on the capture overlay (Lightshot style) before finishing.</summary>
    public bool EditAfterSelection { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; }
    public MonitorTarget FullScreenTarget { get; set; } = MonitorTarget.UnderCursor;
    public int CaptureDelaySeconds { get; set; }
    public bool ReduceMotion { get; set; }
    /// <summary>Check GitHub for a newer version shortly after startup (no data is sent).</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    public List<string> OcrLanguages { get; set; } = new() { "eng" };
    public Dictionary<string, string> Hotkeys { get; set; } = new(DefaultHotkeys);

    public static readonly IReadOnlyDictionary<string, string> DefaultHotkeys = new Dictionary<string, string>
    {
        [HotkeyActions.CaptureRegion] = "Alt+Shift+S",
        [HotkeyActions.CaptureFullScreen] = "Alt+Shift+F",
        [HotkeyActions.OpenApp] = "Alt+Shift+O",
    };

    public AppSettings Clone() => new()
    {
        Theme = Theme, SaveDirectory = SaveDirectory, DefaultFormat = DefaultFormat,
        CopyToClipboardAfterCapture = CopyToClipboardAfterCapture, SaveToLibraryAfterCapture = SaveToLibraryAfterCapture, EditAfterSelection = EditAfterSelection,
        OpenEditorAfterCapture = OpenEditorAfterCapture, FullScreenTarget = FullScreenTarget,
        CaptureDelaySeconds = CaptureDelaySeconds, ReduceMotion = ReduceMotion,
        AutoCheckUpdates = AutoCheckUpdates, LastUpdateCheckUtc = LastUpdateCheckUtc,
        OcrLanguages = new List<string>(OcrLanguages), Hotkeys = new Dictionary<string, string>(Hotkeys),
    };
}

public static class HotkeyActions
{
    public const string CaptureRegion = "CaptureRegion";
    public const string CaptureFullScreen = "CaptureFullScreen";
    public const string OpenApp = "OpenApp";
    public static readonly string[] All = { CaptureRegion, CaptureFullScreen, OpenApp };
}
