using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaply.App.Controls;
using Snaply.App.ViewModels;

namespace Snaply.App.Views;

public partial class SettingsPage : UserControl
{
    private const double CompactWidth = 820;
    private static readonly string[] Keys = { "general", "appearance", "capture", "shortcuts", "ocr", "redaction", "storage", "updates", "about" };

    private SettingsViewModel? _vm;
    private DateTime _lockTrackingUntil;
    private bool _syncingPicker;

    // Shortcut recording is handled by MainWindow (window-level tunnelling), see MainWindow.OnRecordKeyDown.
    public SettingsPage()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Attach(DataContext as SettingsViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
        AttachedToVisualTree += (_, _) => Attach(DataContext as SettingsViewModel);
        Loaded += (_, _) => { ApplyCompact(); TryScroll(); };
        PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) ApplyCompact(); };

        this.FindControl<ScrollViewer>("SettingsScroll")!.ScrollChanged += (_, _) => TrackScroll();

        var search = this.FindControl<TextBox>("SettingsSearch")!;
        search.TextChanged += (_, _) => OnSearchChanged();
        this.FindControl<Button>("ClearSearch")!.Click += (_, _) => search.Text = "";
        this.FindControl<Button>("NoResultsClear")!.Click += (_, _) => search.Text = "";

        this.FindControl<ComboBox>("SectionPicker")!.SelectionChanged += (_, _) =>
        {
            if (_syncingPicker || _vm is null) return;
            if (this.FindControl<ComboBox>("SectionPicker")!.SelectedItem is SettingsSectionItem s) _vm.GoToSectionCommand.Execute(s.Key);
        };
    }

    private void Attach(SettingsViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        if (_vm is not null) { _vm.ScrollRequested -= OnScrollRequested; _vm.PropertyChanged -= OnVmPropertyChanged; }
        _vm = vm;
        if (_vm is not null) { _vm.ScrollRequested += OnScrollRequested; _vm.PropertyChanged += OnVmPropertyChanged; SyncPicker(); }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.ActiveSection)) SyncPicker();
    }

    private void SyncPicker()
    {
        if (_vm is null) return;
        var picker = this.FindControl<ComboBox>("SectionPicker")!;
        _syncingPicker = true;
        picker.SelectedItem = _vm.Sections.FirstOrDefault(s => s.Key == _vm.ActiveSection);
        _syncingPicker = false;
    }

    private void ApplyCompact()
    {
        var compact = Bounds.Width > 0 && Bounds.Width < CompactWidth;
        this.FindControl<Border>("SectionNav")!.IsVisible = !compact;
        this.FindControl<ComboBox>("SectionPicker")!.IsVisible = compact;
        this.FindControl<TextBox>("SettingsSearch")!.Width = compact ? 180 : 260;
    }

    private static string SectionName(string key) => char.ToUpperInvariant(key[0]) + key[1..] + "Section";
    private Control? SectionControl(string key) => this.FindControl<Control>(SectionName(key));

    // ---- scrolling ----
    private void OnScrollRequested() => Dispatcher.UIThread.Post(() => TryScroll(), DispatcherPriority.Background);

    /// <summary>Scrolls the requested section to the top of the page. Retries because layout may not be finished yet.</summary>
    private void TryScroll(int attempt = 0)
    {
        if (_vm?.PendingScroll is not { } key) return;
        var section = SectionControl(key);
        var scroll = this.FindControl<ScrollViewer>("SettingsScroll")!;
        var content = this.FindControl<Control>("SettingsContent")!;
        if (section is null || !section.IsVisible) { _vm.PendingScroll = null; return; }

        var origin = section.TranslatePoint(new Point(0, 0), content);
        if (origin is null || (scroll.Extent.Height <= scroll.Viewport.Height && attempt < 3))
        {
            if (attempt < 3) Dispatcher.UIThread.Post(() => TryScroll(attempt + 1), DispatcherPriority.Background);
            return;
        }

        _vm.PendingScroll = null;
        _lockTrackingUntil = DateTime.UtcNow.AddMilliseconds(600); // keep the clicked section highlighted even when it cannot reach the top
        scroll.Offset = new Vector(0, Math.Max(0, origin.Value.Y + content.Margin.Top - 12));
    }

    /// <summary>Highlights the section nearest the top of the viewport while the user scrolls.</summary>
    private void TrackScroll()
    {
        if (_vm is null || DateTime.UtcNow < _lockTrackingUntil) return;
        var scroll = this.FindControl<ScrollViewer>("SettingsScroll")!;
        var content = this.FindControl<Control>("SettingsContent")!;
        string? active = null;
        var visibleKeys = new List<string>();
        foreach (var key in Keys)
        {
            if (SectionControl(key) is not { IsVisible: true } section) continue;
            visibleKeys.Add(key);
            if (section.TranslatePoint(new Point(0, 0), content) is { } p && p.Y + content.Margin.Top <= scroll.Offset.Y + 48) active = key;
        }
        if (visibleKeys.Count == 0) return;
        var atBottom = scroll.Extent.Height > scroll.Viewport.Height && scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
        active = atBottom ? visibleKeys[^1] : active ?? visibleKeys[0];
        if (active != _vm.ActiveSection) _vm.SetActiveSection(active);
    }

    // ---- search ----
    private void OnSearchChanged()
    {
        var search = this.FindControl<TextBox>("SettingsSearch")!;
        this.FindControl<Button>("ClearSearch")!.IsVisible = !string.IsNullOrEmpty(search.Text);
        ApplySearch(search.Text);
    }

    private static bool Contains(string? haystack, string q) => haystack?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private static string TextOf(Control c)
    {
        var parts = new List<string>();
        if (c is SettingsRow r) { parts.Add(r.Title ?? ""); parts.Add(r.Description ?? ""); }
        foreach (var t in c.GetVisualDescendants().OfType<TextBlock>()) if (!string.IsNullOrWhiteSpace(t.Text)) parts.Add(t.Text!);
        foreach (var row in c.GetVisualDescendants().OfType<SettingsRow>()) { parts.Add(row.Title ?? ""); parts.Add(row.Description ?? ""); }
        return string.Join(" ", parts);
    }

    private static bool IsDivider(Control c) => c is Border b && b.Classes.Contains("divider");

    /// <summary>
    /// Filters settings rows by title/description. A section whose own title or description matches stays whole; sections with no
    /// match are hidden (also in the section list). Public so it can be tested.
    /// </summary>
    public void ApplySearch(string? query)
    {
        var q = (query ?? "").Trim();
        var anyVisible = false;

        foreach (var item in _vm?.Sections ?? Enumerable.Empty<SettingsSectionItem>())
        {
            if (SectionControl(item.Key) is not SectionCard card) continue;
            var panel = card.Content as Panel;
            var children = panel?.Children.OfType<Control>().ToList() ?? new List<Control>();

            var sectionMatches = q.Length == 0 || Contains(card.Title, q) || Contains(card.Description, q);
            var anyChildMatch = false;
            foreach (var child in children)
            {
                if (IsDivider(child)) continue;
                var match = q.Length == 0 || sectionMatches || Contains(TextOf(child), q);
                if (child is SettingsRow) child.IsVisible = match;
                if (match) anyChildMatch = true;
            }

            var visible = q.Length == 0 || sectionMatches || anyChildMatch;
            card.IsVisible = visible;
            item.IsVisible = visible;
            anyVisible |= visible;

            // Dividers belong between two visible rows only.
            Border? pending = null; var seen = false;
            foreach (var child in children)
            {
                if (IsDivider(child)) { child.IsVisible = q.Length == 0; pending = (Border)child; continue; }
                if (child is SettingsRow row && !row.IsVisible) continue;
                if (seen && pending is not null && q.Length > 0) pending.IsVisible = true;
                seen = true; pending = null;
            }
        }

        this.FindControl<Control>("NoResults")!.IsVisible = q.Length > 0 && !anyVisible;
    }
}
