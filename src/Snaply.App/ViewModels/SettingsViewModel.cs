using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;
using Snaply.Infrastructure;
using Snaply.Platform;

namespace Snaply.App.ViewModels;

public sealed partial class ShortcutRowViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public ShortcutRowViewModel(SettingsViewModel owner, string action, string label, string description)
    {
        _owner = owner; Action = action; Label = label; Description = description;
    }

    public string Action { get; }
    public string Label { get; }
    public string Description { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Display), nameof(IsBound))] private string _gesture = "";
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _statusIsOk;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Display))] private bool _isRecording;
    [ObservableProperty] private string? _recordError;

    public bool IsBound => !string.IsNullOrWhiteSpace(Gesture);
    public string Display => IsRecording ? "Press the new shortcut…" : IsBound ? Gesture : "Not set";

    [RelayCommand] private void Change() => _owner.BeginRecording(this);
    [RelayCommand] private void CancelRecording() => _owner.EndRecording();
    [RelayCommand] private Task ClearAsync() => _owner.SetGestureAsync(this, "");
    [RelayCommand] private Task ResetAsync() => _owner.SetGestureAsync(this, AppSettings.DefaultHotkeys[Action]);
}

public sealed partial class OcrLanguageRow : ObservableObject
{
    private readonly SettingsViewModel _owner;
    public OcrLanguageRow(SettingsViewModel owner, string code, bool selected) { _owner = owner; Code = code; _isSelected = selected; }
    public string Code { get; }
    public string Name => Code switch { "eng" => "English (eng)", "vie" => "Vietnamese (vie)", _ => Code };

    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => _owner.OnLanguageToggled();
}

/// <summary>One kind of sensitive data the redaction scan can look for; turning it off removes it from suggestions.</summary>
public sealed partial class RedactionRuleRow : ObservableObject
{
    private readonly SettingsViewModel _owner;
    public RedactionRuleRow(SettingsViewModel owner, Snaply.Core.Redaction.SensitiveType type, bool enabled)
    {
        _owner = owner; Type = type; _isEnabled = enabled;
    }
    public Snaply.Core.Redaction.SensitiveType Type { get; }
    public string Name => Snaply.Core.Redaction.SensitiveText.Label(Type);
    [ObservableProperty] private bool _isEnabled;
    partial void OnIsEnabledChanged(bool value) => _owner.OnRedactionRuleToggled();
}

public sealed partial class SettingsSectionItem : ObservableObject
{
    public SettingsSectionItem(string key, string title, string iconKey)
    {
        Key = key; Title = title;
        Icon = (Avalonia.Media.Geometry)Avalonia.Controls.ResourceNodeExtensions.FindResource(Avalonia.Application.Current!, iconKey)!;
    }

    public string Key { get; }
    public string Title { get; }
    public Avalonia.Media.Geometry Icon { get; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly IApplicationSettingsService _settings;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly INotificationService _notes;
    private readonly HotkeyManager _hotkeys;
    private readonly IScreenshotLibrary _library;
    private readonly IScreenshotRepository _repo;
    private readonly IOcrService _ocr;
    private readonly IScreenCaptureService _capture;
    private readonly IShellService _shell;
    private readonly AppPaths _paths;
    private readonly LibraryEvents _events;
    private bool _loading;

    public SettingsViewModel(IApplicationSettingsService settings, ThemeService theme, DialogService dialogs, INotificationService notes,
        HotkeyManager hotkeys, IScreenshotLibrary library, IScreenshotRepository repo, IOcrService ocr, IScreenCaptureService capture,
        IShellService shell, AppPaths paths, LibraryEvents events, UpdateBannerViewModel updates, IStartupRegistration startup)
    {
        _startup = startup;
        _settings = settings; _theme = theme; _dialogs = dialogs; _notes = notes; _hotkeys = hotkeys; _library = library; _repo = repo;
        _ocr = ocr; _capture = capture; _shell = shell; _paths = paths; _events = events;
        Updates = updates;
        BuildSections();

        Shortcuts = new ObservableCollection<ShortcutRowViewModel>
        {
            new(this, HotkeyActions.CaptureRegion, "Capture region", "Drag to select an area of the screen"),
            new(this, HotkeyActions.CaptureFullScreen, "Capture full screen", "Capture the whole monitor"),
            new(this, HotkeyActions.OpenApp, "Open Snaply", "Bring the Snaply window to the front"),
        };
        _hotkeys.StatusChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshShortcutStatus);
        Refresh();
    }

    public UpdateBannerViewModel Updates { get; }

    /// <summary>Asks the settings page to scroll a section to the top ("updates"). Pending until the page is on screen.</summary>
    public string? PendingScroll { get; set; }

    public ObservableCollection<SettingsSectionItem> Sections { get; } = new();
    [ObservableProperty] private string _activeSection = "appearance";

    private void BuildSections()
    {
        Sections.Add(new("appearance", "Appearance", "IconSun"));
        Sections.Add(new("capture", "Capture", "IconCapture"));
        Sections.Add(new("shortcuts", "Shortcuts", "IconKeyboard"));
        Sections.Add(new("ocr", "Text recognition", "IconOcr"));
        Sections.Add(new("redaction", "Redaction", "IconShield"));
        Sections.Add(new("storage", "Storage", "IconFolder"));
        Sections.Add(new("updates", "Updates", "IconRefresh"));
        Sections.Add(new("about", "About", "IconInfo"));
        SetActiveSection("appearance");
    }

    /// <summary>Highlights a section in the list (called when the user scrolls or picks one).</summary>
    public void SetActiveSection(string key)
    {
        ActiveSection = key;
        foreach (var s in Sections) s.IsActive = s.Key == key;
    }

    [RelayCommand]
    private void GoToSection(string? key)
    {
        if (key is null) return;
        SetActiveSection(key);
        RequestScroll(key);
    }
    public event Action? ScrollRequested;
    public void RequestScroll(string section) { PendingScroll = section; SetActiveSection(section); ScrollRequested?.Invoke(); }

    public IReadOnlyList<int> DelayOptions { get; } = new[] { 0, 3, 5, 10 };
    public IReadOnlyList<string> FormatOptions { get; } = new[] { "PNG (lossless)", "JPEG (smaller)" };
    public ObservableCollection<ShortcutRowViewModel> Shortcuts { get; }
    public ObservableCollection<OcrLanguageRow> Languages { get; } = new();

    // Appearance
    [ObservableProperty] private bool _isLight;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _isSystem;
    [ObservableProperty] private bool _reduceMotion;
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private bool _runInBackground;
    [ObservableProperty] private bool _startWithWindows;

    // Capture
    [ObservableProperty] private string _saveDirectory = "";
    [ObservableProperty] private bool _usesDefaultDirectory = true;
    [ObservableProperty] private int _formatIndex;
    [ObservableProperty] private bool _saveToLibrary;
    [ObservableProperty] private bool _editAfterSelection;
    [ObservableProperty] private bool _copyToClipboard;
    [ObservableProperty] private bool _openEditorAfter;
    [ObservableProperty] private bool _allMonitors;
    [ObservableProperty] private int _delaySeconds;
    [ObservableProperty] private string? _captureError;

    // Shortcuts
    [ObservableProperty] private string? _shortcutBanner;
    [ObservableProperty] private bool _hasShortcutBanner;

    // OCR
    [ObservableProperty] private string _ocrEngineText = "";
    [ObservableProperty] private string? _ocrNotice;
    [ObservableProperty] private bool _hasOcrNotice;
    [ObservableProperty] private string _tessdataPath = "";

    public ObservableCollection<RedactionRuleRow> RedactionRules { get; } = new();

    internal void OnRedactionRuleToggled()
    {
        if (_loading) return;
        var off = RedactionRules.Where(r => !r.IsEnabled).Select(r => r.Type.ToString()).ToList();
        _ = ApplyAsync(s => s.DisabledRedactionTypes = off);
    }

    /// <summary>Tools that can be preselected on the capture overlay, from the shared tool definition.</summary>
    public static IReadOnlyList<ToolChoice> DefaultToolOptions { get; } = Snaply.Editor.Model.AnnotationToolset.Tools
        .Where(t => t.InOverlay)
        .Select(t => new ToolChoice(t.Kind.ToString(), t.Kind == Snaply.Editor.Model.AnnotationToolKind.Select ? "Select (none)" : t.Name))
        .ToList();
    [ObservableProperty] private ToolChoice _defaultTool = DefaultToolOptions[0];

    // Storage
    [ObservableProperty] private string _storageSummary = "Calculating…";
    [ObservableProperty] private string? _verifyResult;
    [ObservableProperty] private bool _isBusy;
    private LibraryVerification? _lastVerification;
    private readonly IStartupRegistration _startup;
    [ObservableProperty] private bool _canRemoveOrphans;

    // About
    private static readonly string AssemblyVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1.0";
    public string Version => Updates.IsSupported ? AssemblyVersion : AssemblyVersion + " (development build)";
    public string DataDirectory => _paths.DataDirectory;
    public string LogDirectory => _paths.LogsDirectory;
    public string CaptureSupportText => _capture.Support.IsSupported ? (_capture.Support.Note ?? "Available") : _capture.Support.UnsupportedReason ?? "Unavailable";
    public bool CanOpenFolders => true;

    public override Task OnNavigatedToAsync()
    {
        Refresh();
        return RefreshStorageAsync();
    }

    private void Refresh()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            IsLight = s.Theme == ThemeMode.Light; IsDark = s.Theme == ThemeMode.Dark; IsSystem = s.Theme == ThemeMode.System;
            ReduceMotion = s.ReduceMotion;
            AutoCheckUpdates = s.AutoCheckUpdates;
            RunInBackground = s.RunInBackground;
            StartWithWindows = s.StartWithWindows;
            SaveDirectory = _settings.EffectiveSaveDirectory;
            UsesDefaultDirectory = string.IsNullOrWhiteSpace(s.SaveDirectory);
            FormatIndex = s.DefaultFormat == ImageFormat.Jpeg ? 1 : 0;
            SaveToLibrary = s.SaveToLibraryAfterCapture;
            EditAfterSelection = s.EditAfterSelection;
            CopyToClipboard = s.CopyToClipboardAfterCapture;
            OpenEditorAfter = s.OpenEditorAfterCapture;
            DefaultTool = DefaultToolOptions.FirstOrDefault(t => t.Key == s.DefaultCaptureTool) ?? DefaultToolOptions[0];
            AllMonitors = s.FullScreenTarget == MonitorTarget.AllMonitors;
            DelaySeconds = s.CaptureDelaySeconds;
            RedactionRules.Clear();
            foreach (var type in Enum.GetValues<Snaply.Core.Redaction.SensitiveType>())
                RedactionRules.Add(new RedactionRuleRow(this, type, !s.DisabledRedactionTypes.Contains(type.ToString())));

            foreach (var row in Shortcuts) row.Gesture = s.Hotkeys.GetValueOrDefault(row.Action, "");
            RefreshShortcutStatus();
            RefreshOcr();
        }
        finally { _loading = false; }
    }

    private async Task ApplyAsync(Action<AppSettings> mutate, Action<string>? onError = null)
    {
        if (_loading) return;
        var r = await _settings.UpdateAsync(mutate).ConfigureAwait(true);
        if (!r.IsSuccess)
        {
            onError?.Invoke(r.Error!.Message);
            _notes.Show(NotificationKind.Warning, "Setting not changed", r.Error!.Message);
            Refresh();
        }
        else CaptureError = null;
    }

    // ---- Appearance ----
    partial void OnIsLightChanged(bool value) { if (value) _ = SetThemeAsync(ThemeMode.Light); else EnsureThemeChecked(); }
    partial void OnIsDarkChanged(bool value) { if (value) _ = SetThemeAsync(ThemeMode.Dark); else EnsureThemeChecked(); }
    partial void OnIsSystemChanged(bool value) { if (value) _ = SetThemeAsync(ThemeMode.System); else EnsureThemeChecked(); }

    // Clicking the selected segment would untick it; put the check back so exactly one mode is always selected.
    private void EnsureThemeChecked() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_loading || IsLight || IsDark || IsSystem) return;
        _loading = true;
        var t = _settings.Current.Theme;
        IsLight = t == ThemeMode.Light; IsDark = t == ThemeMode.Dark; IsSystem = t == ThemeMode.System;
        _loading = false;
    });

    private async Task SetThemeAsync(ThemeMode mode)
    {
        if (_loading) return;
        // Radio-like behaviour: untick the others without re-triggering.
        _loading = true; IsLight = mode == ThemeMode.Light; IsDark = mode == ThemeMode.Dark; IsSystem = mode == ThemeMode.System; _loading = false;
        await ApplyAsync(s => s.Theme = mode).ConfigureAwait(true);
        _theme.Apply(_settings.Current.Theme);
    }

    partial void OnRunInBackgroundChanged(bool value) => _ = ApplyAsync(s => s.RunInBackground = value);

    /// <summary>Starting with Windows is offered only where it works: Windows, and the installed app (not a development build).</summary>
    public bool StartupAvailable => _startup.IsSupported && Updates.IsSupported;
    public string StartupNote => !_startup.IsSupported ? "Available on Windows only."
        : !Updates.IsSupported ? "Available in the installed app, not in development builds."
        : "Snaply starts hidden in the system tray when you sign in, so your shortcuts work right away.";

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading || !StartupAvailable) return;
        var result = _startup.Set(value);
        if (!result.IsSuccess)
        {
            _notes.Show(NotificationKind.Warning, "Could not change the startup setting", result.Error!.Message);
            _loading = true; StartWithWindows = !value; _loading = false; // put the switch back
            return;
        }
        _ = ApplyAsync(s => s.StartWithWindows = value);
    }

    partial void OnAutoCheckUpdatesChanged(bool value) => _ = ApplyAsync(s => s.AutoCheckUpdates = value);

    partial void OnReduceMotionChanged(bool value)
    {
        if (_loading) return;
        _ = ApplyAsync(s => s.ReduceMotion = value).ContinueWith(_ => _theme.ApplyMotion(_settings.Current.ReduceMotion), TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- Capture ----
    partial void OnFormatIndexChanged(int value) => _ = ApplyAsync(s => s.DefaultFormat = value == 1 ? ImageFormat.Jpeg : ImageFormat.Png);
    partial void OnSaveToLibraryChanged(bool value) => _ = ApplyAsync(s => s.SaveToLibraryAfterCapture = value, e => CaptureError = e);
    partial void OnEditAfterSelectionChanged(bool value) => _ = ApplyAsync(s => s.EditAfterSelection = value);
    partial void OnCopyToClipboardChanged(bool value) => _ = ApplyAsync(s => s.CopyToClipboardAfterCapture = value);
    partial void OnOpenEditorAfterChanged(bool value) => _ = ApplyAsync(s => s.OpenEditorAfterCapture = value);
    partial void OnAllMonitorsChanged(bool value) => _ = ApplyAsync(s => s.FullScreenTarget = value ? MonitorTarget.AllMonitors : MonitorTarget.UnderCursor);
    partial void OnDefaultToolChanged(ToolChoice value) => _ = ApplyAsync(s => s.DefaultCaptureTool = value.Key);
    partial void OnDelaySecondsChanged(int value) => _ = ApplyAsync(s => s.CaptureDelaySeconds = value);

    [RelayCommand]
    private async Task ChooseDirectoryAsync()
    {
        var path = await _dialogs.PickFolderAsync("Choose where screenshots are saved").ConfigureAwait(true);
        if (path is null) return;
        await ApplyAsync(s => s.SaveDirectory = path, e => CaptureError = e).ConfigureAwait(true);
        Refresh();
        _ = RefreshStorageAsync();
        if (string.Equals(_settings.Current.SaveDirectory, path, StringComparison.Ordinal))
            _notes.Show(NotificationKind.Success, "Save location updated", "Existing screenshots stay where they are.");
    }

    [RelayCommand]
    private async Task ResetDirectoryAsync()
    {
        await ApplyAsync(s => s.SaveDirectory = null).ConfigureAwait(true);
        Refresh();
        _ = RefreshStorageAsync();
    }

    // ---- Shortcuts ----
    private ShortcutRowViewModel? _recording;

    public bool IsRecordingShortcut => _recording is not null;

    public void BeginRecording(ShortcutRowViewModel row)
    {
        EndRecording();
        _recording = row; row.IsRecording = true; row.RecordError = null;
    }

    public void EndRecording()
    {
        if (_recording is null) return;
        _recording.IsRecording = false;
        _recording = null;
    }

    /// <summary>Called by the view for each key press while a row is recording.</summary>
    public async Task<bool> HandleRecordingKeyAsync(Key key, KeyModifiers modifiers)
    {
        var row = _recording;
        if (row is null) return false;
        if (key == Key.Escape) { EndRecording(); return true; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System) return true;

        var name = KeyName(key);
        if (name is null) { row.RecordError = "That key can't be used in a shortcut."; return true; }

        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(name);
        EndRecording();
        await SetGestureAsync(row, string.Join('+', parts)).ConfigureAwait(true);
        return true;
    }

    private static string? KeyName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();
        return key switch
        {
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Home => "Home", Key.End => "End",
            Key.Insert => "Insert", Key.Delete => "Delete", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right", Key.PrintScreen => "PrintScreen",
            _ => null,
        };
    }

    public async Task SetGestureAsync(ShortcutRowViewModel row, string gesture)
    {
        row.RecordError = null;
        var r = await _settings.UpdateAsync(s => s.Hotkeys[row.Action] = gesture).ConfigureAwait(true);
        if (!r.IsSuccess) { row.RecordError = r.Error!.Message; return; }
        row.Gesture = gesture;
        // SettingsChanged already re-applied registrations; statuses refresh through StatusChanged.
        RefreshShortcutStatus();
    }

    private void RefreshShortcutStatus()
    {
        HasShortcutBanner = !_hotkeys.IsSupported;
        ShortcutBanner = _hotkeys.IsSupported ? null : _hotkeys.UnsupportedReason;
        foreach (var row in Shortcuts)
        {
            if (!row.IsBound) { row.StatusText = null; row.StatusIsError = false; row.StatusIsOk = false; continue; }
            if (!_hotkeys.Status.TryGetValue(row.Action, out var st)) { row.StatusText = null; row.StatusIsError = false; row.StatusIsOk = false; continue; }
            row.StatusIsOk = st.IsRegistered;
            row.StatusIsError = !st.IsRegistered && st.Status != HotkeyStatus.Unsupported;
            row.StatusText = st.Status switch
            {
                HotkeyStatus.Registered => "Active",
                HotkeyStatus.Unsupported => "Not available on this system",
                _ => st.Message ?? "Could not be registered",
            };
        }
    }

    // ---- OCR ----
    private void RefreshOcr()
    {
        Languages.Clear();
        var selected = _settings.Current.OcrLanguages;
        foreach (var l in _ocr.GetInstalledLanguages()) Languages.Add(new OcrLanguageRow(this, l, selected.Contains(l, StringComparer.OrdinalIgnoreCase)));
        OcrEngineText = _ocr.IsAvailable ? "Local, offline" : "No language data found";
        TessdataPath = _paths.TessdataDirectory;
        OcrNotice = _ocr.UnavailableReason;
        HasOcrNotice = !string.IsNullOrWhiteSpace(OcrNotice);
    }

    public void OnLanguageToggled()
    {
        if (_loading) return;
        var chosen = Languages.Where(l => l.IsSelected).Select(l => l.Code).ToList();
        if (chosen.Count == 0)
        {
            _notes.Show(NotificationKind.Info, "Keep one language selected", "OCR needs at least one language.");
            RefreshOcr();
            return;
        }
        _ = ApplyAsync(s => s.OcrLanguages = chosen);
    }

    [RelayCommand]
    private void OpenTessdataFolder()
    {
        Directory.CreateDirectory(_paths.TessdataDirectory);
        OpenFolder(_paths.TessdataDirectory);
    }

    // ---- Storage ----
    private async Task RefreshStorageAsync()
    {
        var shots = await _repo.CountAsync(new ScreenshotQuery()).ConfigureAwait(true);
        var bytes = await Task.Run(() => DirSize(_settings.EffectiveSaveDirectory) + DirSize(_paths.ThumbnailsDirectory)).ConfigureAwait(true);
        StorageSummary = $"{shots} screenshot{(shots == 1 ? "" : "s")} · {ScreenshotItemViewModel.FormatSize(bytes)} on disk (images and thumbnails)";
    }

    private static long DirSize(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly).Sum(f => f.Length) : 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    [RelayCommand]
    private async Task VerifyLibraryAsync()
    {
        IsBusy = true;
        try
        {
            var v = await _library.VerifyAsync().ConfigureAwait(true);
            _lastVerification = v;
            var orphans = v.OrphanImageFiles.Count + v.OrphanThumbnails.Count;
            CanRemoveOrphans = orphans > 0;
            VerifyResult = (v.MissingFileScreenshotIds.Count, orphans) switch
            {
                (0, 0) => "Library is consistent: every entry has its file, and there are no stray files.",
                var (m, o) => $"{m} entr{(m == 1 ? "y" : "ies")} point to missing files (shown as missing in the gallery). {o} stray file{(o == 1 ? "" : "s")} not in the library.",
            };
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RemoveOrphansAsync()
    {
        if (_lastVerification is null) return;
        var n = _lastVerification.OrphanImageFiles.Count + _lastVerification.OrphanThumbnails.Count;
        if (!await _dialogs.ConfirmAsync("Remove stray files?", $"{n} file(s) created by Snaply but not part of your library will be deleted.", "Remove", "Cancel", danger: true).ConfigureAwait(true)) return;
        var removed = await _library.DeleteOrphansAsync(_lastVerification).ConfigureAwait(true);
        _notes.Show(NotificationKind.Success, "Stray files removed", $"{removed} deleted");
        await VerifyLibraryAsync().ConfigureAwait(true);
        _ = RefreshStorageAsync();
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        var count = await _repo.CountAsync(new ScreenshotQuery()).ConfigureAwait(true);
        if (count == 0) { _notes.Show(NotificationKind.Info, "Library is already empty"); return; }
        var ok = await _dialogs.ConfirmAsync("Delete all screenshots?",
            $"All {count} screenshots, their thumbnails, tags and edits will be permanently deleted. This cannot be undone.", "Delete everything", "Cancel", danger: true).ConfigureAwait(true);
        if (!ok) return;

        IsBusy = true;
        var failed = 0;
        try
        {
            foreach (var s in await _repo.ListAllAsync().ConfigureAwait(true))
                if (!(await _library.DeleteAsync(s.Id).ConfigureAwait(true)).IsSuccess) failed++;
        }
        finally { IsBusy = false; }

        _events.RaiseChanged();
        _notes.Show(failed == 0 ? NotificationKind.Success : NotificationKind.Warning, failed == 0 ? "All screenshots deleted" : "Deleted with problems",
            failed == 0 ? null : $"{failed} file(s) could not be deleted and were left on disk.");
        _ = RefreshStorageAsync();
    }

    [RelayCommand]
    private async Task ClearOcrTextAsync()
    {
        if (!await _dialogs.ConfirmAsync("Clear extracted text?", "Text extracted from screenshots will be removed from the library. Your images are not affected.", "Clear text", "Cancel", danger: true).ConfigureAwait(true)) return;
        foreach (var s in await _repo.ListAllAsync().ConfigureAwait(true))
            if (s.OcrText is not null) await _repo.SetOcrAsync(s.Id, null, null).ConfigureAwait(true);
        _events.RaiseChanged();
        _notes.Show(NotificationKind.Success, "Extracted text cleared");
    }

    [RelayCommand] private void OpenScreenshotFolder() { Directory.CreateDirectory(_settings.EffectiveSaveDirectory); OpenFolder(_settings.EffectiveSaveDirectory); }
    [RelayCommand] private void OpenDataFolder() => OpenFolder(_paths.DataDirectory);
    [RelayCommand] private void OpenLogFolder() => OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private async Task OpenPermissionSettingsAsync()
    {
        var r = _shell.OpenSystemPermissionSettings();
        if (!r.IsSuccess) _notes.Show(NotificationKind.Info, "No permission needed", r.Error!.Message);
        await Task.CompletedTask;
    }

    public bool NeedsCapturePermission => OperatingSystem.IsMacOS();

    private void OpenFolder(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
            else if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
            else Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _notes.Show(NotificationKind.Warning, "Could not open the folder", path);
        }
    }
}

public sealed record ToolChoice(string Key, string Label);
