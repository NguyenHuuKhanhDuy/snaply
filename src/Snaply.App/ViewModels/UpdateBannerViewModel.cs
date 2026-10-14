using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Services;

namespace Snaply.App.ViewModels;

/// <summary>Presents <see cref="UpdateCoordinator"/> state as the update banner and the About section.</summary>
public sealed partial class UpdateBannerViewModel : ObservableObject
{
    private readonly UpdateCoordinator _coordinator;
    private readonly DialogService _dialogs;

    public UpdateBannerViewModel(UpdateCoordinator coordinator, DialogService dialogs)
    {
        _coordinator = coordinator; _dialogs = dialogs;
        _coordinator.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { if (_coordinator.State == UpdateState.Ready) Refresh(); };
        timer.Start();
    }

    /// <summary>Set by the main view model: true while the editor is open, and a hook that saves/closes it before restarting.</summary>
    public Func<bool> IsEditorOpen { get; set; } = () => false;
    public Func<Task> PrepareForRestart { get; set; } = () => Task.CompletedTask;

    public bool IsSupported => _coordinator.IsSupported;
    public string? UnsupportedReason => _coordinator.UnsupportedReason;
    public string CurrentVersion => _coordinator.CurrentVersion;

    [ObservableProperty] private bool _bannerVisible;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _showUpdateNow;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _showRestart;
    [ObservableProperty] private bool _showRetry;
    [ObservableProperty] private bool _restartEnabled = true;
    [ObservableProperty] private bool _showInstallLater;
    [ObservableProperty] private bool _hasNotes;

    // About section
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isChecking;
    public bool CanCheck => IsSupported && !IsChecking && !_coordinator.IsBusy;

    public string LastCheckedText => _coordinator.LastCheckedUtc is { } t
        ? $"Last checked {t.ToLocalTime():MMM d, HH:mm}"
        : "Not checked yet";

    public void Refresh()
    {
        var c = _coordinator;
        var v = c.Available?.Version;
        BannerVisible = c.BannerVisible;
        Progress = c.Progress;
        HasNotes = !string.IsNullOrWhiteSpace(c.Available?.NotesMarkdown);
        IsChecking = c.State == UpdateState.Checking;

        ShowUpdateNow = c.State == UpdateState.Available;
        ShowProgress = c.State == UpdateState.Downloading;
        ShowRestart = c.State == UpdateState.Ready;
        ShowRetry = c.State == UpdateState.Failed && c.Available is not null;
        ShowInstallLater = c.State == UpdateState.Ready && !c.InstallScheduled;
        RestartEnabled = c.CanRestartNow;

        (Title, Detail) = c.State switch
        {
            UpdateState.Available => ($"Snaply {v} is available", $"You have {c.CurrentVersion}. Updating keeps your screenshots and settings."),
            UpdateState.Downloading => ($"Downloading Snaply {v}…", $"{c.Progress}%"),
            UpdateState.Ready => ($"Snaply {v} is ready to install", c.InstallScheduled ? c.Message
                : c.CanRestartNow ? "Restart to finish updating." : "Finish your capture, then restart to update."),
            UpdateState.Failed when c.Available is not null => ($"Couldn't update to {v}", c.Message),
            _ => ("", null),
        };

        StatusText = c.State switch
        {
            UpdateState.Checking => "Checking for updates…",
            UpdateState.UpToDate => $"You're up to date (version {c.CurrentVersion}).",
            UpdateState.Available => $"Version {v} is available.",
            UpdateState.Downloading => $"Downloading version {v}… {c.Progress}%",
            UpdateState.Ready => $"Version {v} is ready to install.",
            UpdateState.Failed => c.Message ?? "The update check failed.",
            _ => c.Message ?? (c.IsSupported ? $"Version {c.CurrentVersion}" : c.UnsupportedReason ?? ""),
        };

        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(LastCheckedText));
    }

    [RelayCommand] private Task CheckNowAsync() => _coordinator.CheckAsync(manual: true);
    [RelayCommand] private Task UpdateNowAsync() => _coordinator.DownloadAsync();
    [RelayCommand] private void Later() => _coordinator.Dismiss();
    [RelayCommand] private void InstallOnNextLaunch() => _coordinator.InstallOnNextLaunch();

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (!_coordinator.CanRestartNow) return;
        if (IsEditorOpen())
        {
            var ok = await _dialogs.ConfirmAsync("Restart to update?",
                "Your edits are saved automatically and will be there when Snaply starts again.", "Restart now", "Cancel").ConfigureAwait(true);
            if (!ok) return;
        }
        await PrepareForRestart().ConfigureAwait(true);
        _coordinator.RestartNow();
    }

    [RelayCommand]
    private async Task ShowNotesAsync()
    {
        var u = _coordinator.Available;
        if (u is null) return;
        var notes = string.IsNullOrWhiteSpace(u.NotesMarkdown) ? "No release notes were provided." : u.NotesMarkdown!.Trim();
        await _dialogs.ConfirmAsync($"What's new in {u.Version}", notes.Length > 1200 ? notes[..1200] + "…" : notes, "Close", null).ConfigureAwait(true);
    }
}
