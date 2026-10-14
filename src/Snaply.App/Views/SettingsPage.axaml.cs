using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Snaply.App.ViewModels;

namespace Snaply.App.Views;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel? _vm;

    // Shortcut recording is handled by MainWindow (window-level tunnelling), see MainWindow.OnRecordKeyDown.
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.ScrollRequested -= OnScrollRequested;
            _vm = DataContext as SettingsViewModel;
            if (_vm is not null) _vm.ScrollRequested += OnScrollRequested;
        };
        DetachedFromVisualTree += (_, _) => { if (_vm is not null) _vm.ScrollRequested -= OnScrollRequested; };
        AttachedToVisualTree += (_, _) => { if (_vm is not null) { _vm.ScrollRequested -= OnScrollRequested; _vm.ScrollRequested += OnScrollRequested; } };
        Loaded += (_, _) => TryScroll();
    }

    private void OnScrollRequested() => Dispatcher.UIThread.Post(() => TryScroll(), DispatcherPriority.Background);

    /// <summary>Scrolls the requested section to the top of the page. Retries once because layout may not be finished yet.</summary>
    private void TryScroll(int attempt = 0)
    {
        if (_vm?.PendingScroll is not { } target) return;
        var section = target == "updates" ? this.FindControl<Control>("UpdatesSection") : null;
        var scroll = this.FindControl<ScrollViewer>("SettingsScroll");
        var content = this.FindControl<Control>("SettingsContent");
        if (section is null || scroll is null || content is null) { _vm.PendingScroll = null; return; }

        var origin = section.TranslatePoint(new Point(0, 0), content);
        if (origin is null || scroll.Extent.Height <= scroll.Viewport.Height && attempt < 3)
        {
            if (attempt < 3) Dispatcher.UIThread.Post(() => TryScroll(attempt + 1), DispatcherPriority.Background);
            return;
        }

        _vm.PendingScroll = null;
        // Keep a little of the previous section's spacing above the title.
        scroll.Offset = new Vector(0, Math.Max(0, origin.Value.Y + content.Margin.Top - 12));
    }
}
