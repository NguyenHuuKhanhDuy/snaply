namespace Snaply.Core.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>Colour theme id (see ColorPalettes); unknown or empty means the default blue.</summary>
    public string ColorTheme { get; set; } = "default";
    public string? SaveDirectory { get; set; }
    public ImageFormat DefaultFormat { get; set; } = ImageFormat.Png;
    public bool CopyToClipboardAfterCapture { get; set; }
    /// <summary>Auto-save: store every capture in the library as soon as it is finished.</summary>
    public bool SaveToLibraryAfterCapture { get; set; } = true;
    /// <summary>After choosing a region, annotate it in place on the capture overlay before finishing.</summary>
    public bool EditAfterSelection { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; }
    /// <summary>Tool preselected on the capture overlay once an area is chosen (a tool key such as "Rectangle"; "Select" = none).</summary>
    public string DefaultCaptureTool { get; set; } = "Select";
    public MonitorTarget FullScreenTarget { get; set; } = MonitorTarget.UnderCursor;
    public int CaptureDelaySeconds { get; set; }
    public bool ReduceMotion { get; set; }
    /// <summary>Check online for a newer version shortly after startup (no data is sent).</summary>
    /// <summary>Closing the window hides it to the system tray so global shortcuts keep working.</summary>
    public bool RunInBackground { get; set; } = true;
    /// <summary>Start Snaply (hidden in the tray) when the user signs in to Windows. Off unless the user turns it on.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>Store the title of the captured window with each screenshot (Windows only, local only). On by default.</summary>
    public bool RememberSourceWindow { get; set; } = true;
    /// <summary>The user hid the getting-started checklist on Home.</summary>
    public bool OnboardingDismissed { get; set; }
    public GalleryViewMode GalleryViewMode { get; set; } = GalleryViewMode.Grid;
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Sensitive-data types (names of <c>SensitiveType</c>) the user turned off for redaction suggestions.</summary>
    public List<string> DisabledRedactionTypes { get; set; } = new() { "Uuid" };
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
        Theme = Theme, ColorTheme = ColorTheme, SaveDirectory = SaveDirectory, DefaultFormat = DefaultFormat,
        CopyToClipboardAfterCapture = CopyToClipboardAfterCapture, SaveToLibraryAfterCapture = SaveToLibraryAfterCapture, EditAfterSelection = EditAfterSelection,
        OpenEditorAfterCapture = OpenEditorAfterCapture, DefaultCaptureTool = DefaultCaptureTool, FullScreenTarget = FullScreenTarget,
        CaptureDelaySeconds = CaptureDelaySeconds, ReduceMotion = ReduceMotion,
        RunInBackground = RunInBackground, StartWithWindows = StartWithWindows, RememberSourceWindow = RememberSourceWindow, OnboardingDismissed = OnboardingDismissed, GalleryViewMode = GalleryViewMode, AutoCheckUpdates = AutoCheckUpdates, LastUpdateCheckUtc = LastUpdateCheckUtc,
        OcrLanguages = new List<string>(OcrLanguages), DisabledRedactionTypes = new List<string>(DisabledRedactionTypes), Hotkeys = new Dictionary<string, string>(Hotkeys),
    };
}

public static class HotkeyActions
{
    public const string CaptureRegion = "CaptureRegion";
    public const string CaptureFullScreen = "CaptureFullScreen";
    public const string OpenApp = "OpenApp";
    public static readonly string[] All = { CaptureRegion, CaptureFullScreen, OpenApp };
}
