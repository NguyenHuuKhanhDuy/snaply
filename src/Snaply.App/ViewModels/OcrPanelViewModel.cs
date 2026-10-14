using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;
using Snaply.Core.Services;

namespace Snaply.App.ViewModels;

/// <summary>Runs OCR for one screenshot and shows/copies the result. Reused by the gallery details panel and the editor.</summary>
public sealed partial class OcrPanelViewModel : ObservableObject
{
    private readonly OcrWorkflow _workflow;
    private readonly IApplicationSettingsService _settings;
    private readonly INotificationService _notes;
    private readonly ScreenshotActions _actions;
    private readonly LibraryEvents _events;
    private CancellationTokenSource? _cts;
    private Screenshot? _shot;

    public OcrPanelViewModel(OcrWorkflow workflow, IApplicationSettingsService settings, INotificationService notes, ScreenshotActions actions, LibraryEvents events)
    {
        _workflow = workflow; _settings = settings; _notes = notes; _actions = actions; _events = events;
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasText), nameof(RunLabel), nameof(ShowEmptyHint))] private string? _text;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRun), nameof(ShowEmptyHint))] private bool _isRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _errorMessage;
    [ObservableProperty] private string? _infoMessage;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsAvailable => _workflow.Engine.IsAvailable;
    public string? UnavailableReason => _workflow.Engine.UnavailableReason;
    public bool CanRun => _shot is not null && !IsRunning && IsAvailable;
    public string RunLabel => HasText ? "Re-run" : "Extract text";
    public bool ShowEmptyHint => !HasText && !IsRunning && !HasError && IsAvailable;
    public string LanguageSummary => string.Join(" + ", _settings.Current.OcrLanguages);

    public void SetScreenshot(Screenshot? shot)
    {
        _cts?.Cancel();
        _shot = shot;
        Text = shot?.OcrText;
        ErrorMessage = null;
        InfoMessage = shot?.OcrText is { Length: 0 } ? "No text was found in this screenshot." : null;
        IsRunning = false;
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(LanguageSummary));
        RunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_shot is null) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var shot = _shot;

        IsRunning = true; ErrorMessage = null; InfoMessage = null;
        RunCommand.NotifyCanExecuteChanged();
        try
        {
            var r = await _workflow.RunAsync(shot.Id, _settings.Current.OcrLanguages, ct).ConfigureAwait(true);
            if (_shot?.Id != shot.Id) return; // user moved on to another screenshot

            if (r.IsSuccess)
            {
                Text = r.Value.Text;
                shot.OcrText = r.Value.Text;
                InfoMessage = HasText ? null : "No text was found in this screenshot.";
                _notes.Show(NotificationKind.Success, "Text extracted", HasText ? $"{r.Value.Text.Length} characters" : "No text found");
                _events.RaiseChanged();
            }
            else if (!r.IsCancelled)
            {
                ErrorMessage = r.Error!.Message;
            }
        }
        catch (OperationCanceledException) { /* cancelled: nothing stored */ }
        finally
        {
            IsRunning = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task CopyAsync()
    {
        if (HasText) await _actions.CopyTextAsync(Text!, "Extracted text copied").ConfigureAwait(true);
    }
}
