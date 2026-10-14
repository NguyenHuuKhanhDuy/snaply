using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Common;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.ViewModels;

/// <summary>Contextual panel for the selected screenshot: preview, properties, tags, actions and OCR.</summary>
public sealed partial class DetailsViewModel : ObservableObject
{
    private readonly IScreenshotRepository _repo;
    private readonly IScreenshotLibrary _library;
    private readonly ScreenshotActions _actions;
    private readonly NavigationHub _hub;
    private readonly LibraryEvents _events;
    private readonly INotificationService _notes;
    private int _version;

    public DetailsViewModel(IScreenshotRepository repo, IScreenshotLibrary library, ScreenshotActions actions, NavigationHub hub, LibraryEvents events,
        INotificationService notes, OcrPanelViewModel ocr)
    {
        _repo = repo; _library = library; _actions = actions; _hub = hub; _events = events; _notes = notes;
        Ocr = ocr;
    }

    public event Action<Screenshot>? Deleted;

    public OcrPanelViewModel Ocr { get; }
    public ObservableCollection<TagViewModel> Tags { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsOpen), nameof(FileName), nameof(Dimensions), nameof(SizeText), nameof(CreatedText), nameof(PathText), nameof(FormatText))]
    private Screenshot? _shot;
    [ObservableProperty] private Bitmap? _preview;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewUnavailable))] private bool _isMissing;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _newTag = "";
    [ObservableProperty] private string? _tagError;

    public bool IsOpen => Shot is not null;
    public bool CanReveal => _actions.CanReveal;
    public bool PreviewUnavailable => Preview is null && Shot is not null;
    public string FileName => Shot?.FileName ?? "";
    public string Dimensions => Shot is null ? "" : $"{Shot.Width} × {Shot.Height} px";
    public string SizeText => Shot is null ? "" : ScreenshotItemViewModel.FormatSize(Shot.FileSize);
    public string CreatedText => Shot?.CreatedAt.ToLocalTime().ToString("MMM d, yyyy · HH:mm") ?? "";
    public string PathText => Shot?.FilePath ?? "";
    public string FormatText => Shot?.Format.ToUpperInvariant() ?? "";

    public async void SetScreenshot(Screenshot? shot)
    {
        var version = Interlocked.Increment(ref _version);
        var old = Preview;
        Preview = null;
        old?.Dispose();

        Shot = shot;
        Tags.Clear();
        NewTag = ""; TagError = null;
        Ocr.SetScreenshot(shot);
        OnPropertyChanged(nameof(PreviewUnavailable));
        if (shot is null) return;

        IsFavorite = shot.IsFavorite;
        IsMissing = !File.Exists(shot.FilePath);

        if (!IsMissing)
        {
            var bmp = await Task.Run(() => ImageBridge.LoadScaled(shot.FilePath, 640)).ConfigureAwait(true);
            if (version != _version) { bmp?.Dispose(); return; }
            Preview = bmp;
            OnPropertyChanged(nameof(PreviewUnavailable));
        }

        var tags = await _repo.GetTagsForAsync(shot.Id).ConfigureAwait(true);
        if (version != _version) return;
        foreach (var t in tags) Tags.Add(new TagViewModel(t));
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Shot is null) return;
        var value = !IsFavorite;
        await _actions.SetFavoriteAsync(Shot, value).ConfigureAwait(true);
        IsFavorite = value;
    }

    [RelayCommand]
    private void OpenEditor()
    {
        if (Shot is null) return;
        if (IsMissing) { _actions.MissingFile(); return; }
        _hub.OpenEditor?.Invoke(Shot.Id);
    }

    [RelayCommand] private Task CopyImageAsync() => Shot is null ? Task.CompletedTask : _actions.CopyImageAsync(Shot);
    [RelayCommand] private Task ExportAsync() => Shot is null ? Task.CompletedTask : _actions.ExportAsync(Shot);
    [RelayCommand] private void Reveal() { if (Shot is not null) _actions.Reveal(Shot); }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Shot is null) return;
        var shot = Shot;
        if (await _actions.DeleteAsync(shot).ConfigureAwait(true)) Deleted?.Invoke(shot);
    }

    [RelayCommand]
    private async Task RemoveEntryAsync()
    {
        if (Shot is null) return;
        var shot = Shot;
        var r = await _library.DeleteAsync(shot.Id).ConfigureAwait(true);
        _events.RaiseChanged();
        Deleted?.Invoke(shot);
        _notes.Show(NotificationKind.Info, "Removed from library", r.IsSuccess ? null : r.Error!.Message);
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (Shot is null) return;
        var name = NewTag.Trim();
        if (name.Length == 0) return;
        if (name.Length > 50) { TagError = "Tags can be at most 50 characters."; return; }

        var tag = await _repo.GetOrCreateTagAsync(name).ConfigureAwait(true);
        await _repo.AddTagAsync(Shot.Id, tag.Id).ConfigureAwait(true);
        if (Tags.All(t => t.Tag.Id != tag.Id)) Tags.Add(new TagViewModel(tag));
        NewTag = ""; TagError = null;
        _events.RaiseChanged();
    }

    [RelayCommand]
    private async Task RemoveTagAsync(TagViewModel? tag)
    {
        if (Shot is null || tag is null) return;
        await _repo.RemoveTagAsync(Shot.Id, tag.Tag.Id).ConfigureAwait(true);
        Tags.Remove(tag);
        _events.RaiseChanged();
    }

    [RelayCommand]
    private void Close() => SetScreenshot(null);
}

public sealed class TagViewModel
{
    public TagViewModel(Tag tag) => Tag = tag;
    public Tag Tag { get; }
    public string Name => Tag.Name;
}
