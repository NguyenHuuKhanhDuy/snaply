using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snaply.App.ViewModels;

namespace Snaply.App.Controls;

/// <summary>
/// Thumbnail card. A plain click runs <see cref="ClickCommand"/> with the item; when <see cref="ModifierClickCommand"/> is set it runs that
/// instead with a <see cref="TileClick"/> carrying the Ctrl/Shift state, so the gallery can do multi-select. When <see cref="Actions"/> is
/// set, hovering (or focusing) the card shows quick Copy, Favorite and More buttons that act on this item through it.
/// </summary>
public partial class ScreenshotTile : UserControl
{
    public static readonly StyledProperty<ICommand?> ClickCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(ClickCommand));
    public static readonly StyledProperty<ICommand?> ModifierClickCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(ModifierClickCommand));
    public static readonly StyledProperty<ICommand?> CheckCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(CheckCommand));
    public static readonly StyledProperty<GalleryViewModel?> ActionsProperty = AvaloniaProperty.Register<ScreenshotTile, GalleryViewModel?>(nameof(Actions));

    private KeyModifiers _mods;

    public ScreenshotTile()
    {
        InitializeComponent();
        var root = this.FindControl<Button>("Root")!;
        root.AddHandler(PointerPressedEvent, (_, e) => _mods = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.Click += (_, _) =>
        {
            if (DataContext is ScreenshotItemViewModel item)
            {
                if (ModifierClickCommand is { } m) m.Execute(new TileClick(item, _mods));
                else ClickCommand?.Execute(item);
            }
            _mods = KeyModifiers.None;
        };
        this.FindControl<Button>("CheckButton")!.Click += (_, e) =>
        {
            e.Handled = true;
            if (DataContext is ScreenshotItemViewModel item) CheckCommand?.Execute(item);
        };

        // Quick actions: never open or select the card, only act on it.
        Wire("CopyButton", item => Actions?.CopyItemCommand.Execute(item));
        Wire("FavoriteButton", item => Actions?.ToggleFavoriteItemCommand.Execute(item));
        var more = this.FindControl<Button>("MoreButton")!;
        more.Click += (_, e) =>
        {
            e.Handled = true;
            if (DataContext is ScreenshotItemViewModel item) ShowMoreMenu(more, item);
        };
    }

    private void Wire(string name, Action<ScreenshotItemViewModel> run)
    {
        this.FindControl<Button>(name)!.Click += (_, e) =>
        {
            e.Handled = true;
            if (DataContext is ScreenshotItemViewModel item && Actions is not null) run(item);
        };
    }

    private void ShowMoreMenu(Control anchor, ScreenshotItemViewModel item)
    {
        if (Actions is not { } gallery) return;
        var menu = new MenuFlyout();
        void Add(string header, Action run, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
        }

        Add("Open in editor", () => gallery.OpenItemInEditorCommand.Execute(item));
        if (gallery.CanReveal) Add("Reveal in folder", () => gallery.RevealItemCommand.Execute(item));
        Add("Export…", () => gallery.ExportItemCommand.Execute(item));
        menu.Items.Add(new Separator());
        Add("Delete…", () => gallery.DeleteItemCommand.Execute(item));
        menu.ShowAt(anchor);
    }

    public ICommand? ClickCommand { get => GetValue(ClickCommandProperty); set => SetValue(ClickCommandProperty, value); }
    public ICommand? ModifierClickCommand { get => GetValue(ModifierClickCommandProperty); set => SetValue(ModifierClickCommandProperty, value); }
    public ICommand? CheckCommand { get => GetValue(CheckCommandProperty); set => SetValue(CheckCommandProperty, value); }
    public GalleryViewModel? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
}
