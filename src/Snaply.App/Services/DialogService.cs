using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Snaply.App.Services;

public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public DialogViewModel(string title, string message, string confirmText, string? cancelText, bool isDanger, bool hasInput, string? inputText, string? inputWatermark)
    {
        Title = title; Message = message; ConfirmText = confirmText; CancelText = cancelText;
        IsDanger = isDanger; HasInput = hasInput; _inputText = inputText ?? ""; InputWatermark = inputWatermark;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string? CancelText { get; }
    public bool HasCancel => CancelText is not null;
    public bool IsDanger { get; }
    public bool IsPrimary => !IsDanger;
    public bool HasInput { get; }
    public string? InputWatermark { get; }

    [ObservableProperty] private string _inputText;

    public Task<bool> Result => _tcs.Task;

    [RelayCommand] private void Confirm() => _tcs.TrySetResult(true);
    [RelayCommand] private void Cancel() => _tcs.TrySetResult(false);
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string? cancelText = "Cancel", bool danger = false);
    Task<string?> PromptAsync(string title, string message, string confirmText = "OK", string? initial = null, string? watermark = null);
    Task<string?> PickSaveFileAsync(string suggestedName, string extension, string filterName);
    Task<string?> PickFolderAsync(string title);
}

/// <summary>Themed in-window dialogs (rendered by the main window's dialog host) plus native file pickers.</summary>
public sealed partial class DialogService : ObservableObject, IDialogService
{
    [ObservableProperty] private DialogViewModel? _current;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string? cancelText = "Cancel", bool danger = false)
    {
        var vm = new DialogViewModel(title, message, confirmText, cancelText, danger, false, null, null);
        return await ShowAsync(vm).ConfigureAwait(true);
    }

    public async Task<string?> PromptAsync(string title, string message, string confirmText = "OK", string? initial = null, string? watermark = null)
    {
        var vm = new DialogViewModel(title, message, confirmText, "Cancel", false, true, initial, watermark);
        return await ShowAsync(vm).ConfigureAwait(true) ? vm.InputText : null;
    }

    private async Task<bool> ShowAsync(DialogViewModel vm)
    {
        // One dialog at a time; a second request waits for the first.
        while (Current is not null) await Task.Delay(50).ConfigureAwait(true);
        Current = vm;
        try { return await vm.Result.ConfigureAwait(true); }
        finally { Current = null; }
    }

    public void CancelCurrent() => Current?.CancelCommand.Execute(null);

    private static TopLevel? Top =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension, string filterName)
    {
        if (Top?.StorageProvider is not { CanSave: true } sp) return null;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = new[] { "*." + extension } } },
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Top?.StorageProvider is not { CanPickFolder: true } sp) return null;
        var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
