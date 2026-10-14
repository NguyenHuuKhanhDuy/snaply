using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Snaply.App.Services;
using Snaply.Core.Interfaces;
using Snaply.Core.Models;

namespace Snaply.App.ViewModels;

public sealed partial class TagListItem : ObservableObject
{
    public TagListItem(Tag tag, int count) { Tag = tag; Count = count; }
    public Tag Tag { get; }
    public string Name => Tag.Name;
    public int Count { get; }
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class TagsViewModel : ViewModelBase
{
    private readonly IScreenshotRepository _repo;
    private readonly DialogService _dialogs;
    private readonly LibraryEvents _events;
    private readonly INotificationService _notes;

    public TagsViewModel(IScreenshotRepository repo, DialogService dialogs, LibraryEvents events, INotificationService notes, GalleryViewModel gallery)
    {
        _repo = repo; _dialogs = dialogs; _events = events; _notes = notes;
        Gallery = gallery;
        Gallery.Configure(GalleryMode.Tag, "Tag");
        _events.Changed += () => { if (IsActive) _ = LoadAsync(); };
    }

    public GalleryViewModel Gallery { get; }
    public bool IsActive { get; set; }
    public ObservableCollection<TagListItem> Tags { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private TagListItem? _selectedTag;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowNoTags))] private bool _loaded;

    public bool HasSelection => SelectedTag is not null;
    public bool ShowNoTags => Loaded && Tags.Count == 0;

    public override Task OnNavigatedToAsync() => LoadAsync();

    public async Task SelectTagByIdAsync(long id)
    {
        await LoadAsync().ConfigureAwait(true);
        SelectedTag = Tags.FirstOrDefault(t => t.Tag.Id == id) ?? SelectedTag;
    }

    private async Task LoadAsync()
    {
        var selectedId = SelectedTag?.Tag.Id;
        var tags = await _repo.ListTagsAsync().ConfigureAwait(true);
        var items = new List<TagListItem>();
        foreach (var t in tags) items.Add(new TagListItem(t, await _repo.CountAsync(new ScreenshotQuery(TagId: t.Id)).ConfigureAwait(true)));

        Tags.Clear();
        foreach (var i in items) Tags.Add(i);
        Loaded = true;
        OnPropertyChanged(nameof(ShowNoTags));
        SelectedTag = Tags.FirstOrDefault(t => t.Tag.Id == selectedId) ?? Tags.FirstOrDefault();
        if (SelectedTag is null) Gallery.Configure(GalleryMode.Tag, "Tag", -1);
    }

    partial void OnSelectedTagChanged(TagListItem? oldValue, TagListItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is null) return;
        newValue.IsSelected = true;
        Gallery.IsActive = true;
        Gallery.Configure(GalleryMode.Tag, newValue.Name, newValue.Tag.Id);
        _ = Gallery.ReloadAsync();
    }

    [RelayCommand] private void Select(TagListItem? tag) { if (tag is not null) SelectedTag = tag; }

    [RelayCommand]
    private async Task DeleteTagAsync(TagListItem? tag)
    {
        if (tag is null) return;
        var ok = await _dialogs.ConfirmAsync($"Delete tag '{tag.Name}'?", "The tag is removed from all screenshots. The screenshots themselves are not deleted.", "Delete tag", "Cancel", danger: true).ConfigureAwait(true);
        if (!ok) return;
        await _repo.DeleteTagAsync(tag.Tag.Id).ConfigureAwait(true);
        _notes.Show(NotificationKind.Success, "Tag deleted");
        _events.RaiseChanged();
    }
}
