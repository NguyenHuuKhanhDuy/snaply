using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.ViewModels;

/// <summary>One line of the getting-started checklist. Completion is computed from real state, never stored as "seen".</summary>
public sealed class OnboardingStep
{
    public OnboardingStep(string title, string description, bool isDone, string actionText, Action action)
    {
        Title = title; Description = description; IsDone = isDone; ActionText = actionText; Action = action;
    }

    public string Title { get; }
    public string Description { get; }
    public bool IsDone { get; }
    public bool IsOpen => !IsDone;
    public string ActionText { get; }
    public Action Action { get; }
}

/// <summary>State of the Library header: quick capture actions, shortcut hints and the getting-started card. (Formerly the Home page.)</summary>
public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly CaptureCoordinator _capture;
    private readonly IScreenshotRepository _repo;
    private readonly IScreenshotLibrary _library;
    private readonly IApplicationSettingsService _settings;
    private readonly IScreenCaptureService _captureService;
    private readonly NavigationHub _hub;
    private readonly LibraryEvents _events;
    private readonly Core.Services.HotkeyManager _hotkeys;
    private readonly IOcrService _ocr;

    public HomeViewModel(CaptureCoordinator capture, IScreenshotRepository repo, IScreenshotLibrary library, IApplicationSettingsService settings,
        IScreenCaptureService captureService, NavigationHub hub, LibraryEvents events, Core.Services.HotkeyManager hotkeys, IOcrService ocr)
    {
        _capture = capture; _repo = repo; _library = library; _settings = settings; _captureService = captureService;
        _hub = hub; _events = events; _hotkeys = hotkeys; _ocr = ocr;
        _events.Changed += () => { if (IsActive) _ = LoadAsync(); };
        _hotkeys.StatusChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { OnPropertyChanged(nameof(RegionShortcut)); OnPropertyChanged(nameof(FullScreenShortcut)); OnPropertyChanged(nameof(ShortcutNote)); OnPropertyChanged(nameof(HasShortcutNote)); RefreshStatus(); });
    }

    public bool IsActive { get; set; }

    /// <summary>Opens Settings scrolled to a section ("shortcuts", "ocr", ...). Set by the main view model.</summary>
    public Action<string>? OpenSettingsSection { get; set; }

    public ObservableCollection<OnboardingStep> Steps { get; } = new();
    public ObservableCollection<string> ShortcutIssues { get; } = new();

    [ObservableProperty] private bool _showChecklist;
    [ObservableProperty] private string _checklistTitle = "";
    public bool HasShortcutIssues => ShortcutIssues.Count > 0;

    /// <summary>Recomputes the checklist and shortcut problems from real state (library, hotkey registrations, OCR data).</summary>
    public void RefreshStatus()
    {
        var statuses = _hotkeys.Status;
        var problems = new List<string>();
        foreach (var (action, st) in statuses)
        {
            if (st.Status is HotkeyStatus.Conflict or HotkeyStatus.Failed)
                problems.Add($"{ActionLabel(action)}: {st.Message ?? "could not be registered"}");
        }
        ShortcutIssues.Clear();
        foreach (var p in problems) ShortcutIssues.Add(p);
        OnPropertyChanged(nameof(HasShortcutIssues));

        var shortcutsOk = _hotkeys.IsSupported && statuses.Count > 0 && statuses.Values.All(s => s.IsRegistered);
        var steps = new List<OnboardingStep>
        {
            new("Take your first capture", "Use the Capture region button or your shortcut. It appears below.", TotalCount > 0, "Capture now", () => _ = _capture.CaptureRegionAsync()),
            new("Check your keyboard shortcuts", _hotkeys.IsSupported
                    ? (shortcutsOk ? "Your capture shortcuts are active." : "Some shortcuts could not be registered.")
                    : "Global shortcuts are not available on this system.",
                shortcutsOk, "Open shortcuts", () => OpenSettingsSection?.Invoke("shortcuts")),
            new("Set up text recognition", _ocr.IsAvailable ? "OCR language data was found." : "Add language data so Snaply can read text in screenshots.",
                _ocr.IsAvailable, "Open OCR settings", () => OpenSettingsSection?.Invoke("ocr")),
        };

        Steps.Clear();
        foreach (var s in steps) Steps.Add(s);
        var done = steps.Count(s => s.IsDone);
        ChecklistTitle = $"Get started · {done} of {steps.Count} done";
        ShowChecklist = Loaded && !_settings.Current.OnboardingDismissed && done < steps.Count;
    }

    private static string ActionLabel(string action) => action switch
    {
        HotkeyActions.CaptureRegion => "Capture region",
        HotkeyActions.CaptureFullScreen => "Capture full screen",
        HotkeyActions.OpenApp => "Open Snaply",
        _ => action,
    };

    [RelayCommand]
    private async Task DismissChecklistAsync()
    {
        await _settings.UpdateAsync(s => s.OnboardingDismissed = true).ConfigureAwait(true);
        RefreshStatus();
    }

    [RelayCommand] private void RunStep(OnboardingStep? step) => step?.Action();
    [RelayCommand] private void FixShortcuts() => OpenSettingsSection?.Invoke("shortcuts");
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _loaded;

    public bool CaptureSupported => _captureService.Support.IsSupported;
    public string? CaptureUnsupportedReason => _captureService.Support.UnsupportedReason;
    public string RegionShortcut => ShortcutText(HotkeyActions.CaptureRegion);
    public string FullScreenShortcut => ShortcutText(HotkeyActions.CaptureFullScreen);
    public bool HasShortcutNote => !_hotkeys.IsSupported;
    public string? ShortcutNote => _hotkeys.IsSupported ? null : _hotkeys.UnsupportedReason;
    public bool CanRepeat => _capture.CanRepeat;

    private string ShortcutText(string action) =>
        _hotkeys.IsSupported && _settings.Current.Hotkeys.TryGetValue(action, out var g) && !string.IsNullOrWhiteSpace(g) ? g : "";

    public override Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(RegionShortcut));
        OnPropertyChanged(nameof(FullScreenShortcut));
        OnPropertyChanged(nameof(CanRepeat));
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        TotalCount = await _repo.CountAsync(new ScreenshotQuery()).ConfigureAwait(true);
        Loaded = true;
        OnPropertyChanged(nameof(CanRepeat));
        RefreshStatus();
    }

    [RelayCommand] private Task CaptureRegionAsync() => _capture.CaptureRegionAsync();
    [RelayCommand] private Task CaptureFullScreenAsync() => _capture.CaptureFullScreenAsync();
    [RelayCommand] private Task CaptureDelayedAsync() => _capture.CaptureRegionAsync(5);
    [RelayCommand] private Task RepeatLastAsync() => _capture.RepeatLastAsync();
}
