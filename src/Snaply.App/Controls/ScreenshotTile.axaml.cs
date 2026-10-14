using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snaply.App.ViewModels;

namespace Snaply.App.Controls;

/// <summary>
/// Thumbnail tile. A plain click runs <see cref="ClickCommand"/> with the item; when <see cref="ModifierClickCommand"/> is set it runs that
/// instead with a <see cref="TileClick"/> carrying the Ctrl/Shift state, so the gallery can do multi-select.
/// </summary>
public partial class ScreenshotTile : UserControl
{
    public static readonly StyledProperty<ICommand?> ClickCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(ClickCommand));
    public static readonly StyledProperty<ICommand?> ModifierClickCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(ModifierClickCommand));
    public static readonly StyledProperty<ICommand?> CheckCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(CheckCommand));

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
    }

    public ICommand? ClickCommand { get => GetValue(ClickCommandProperty); set => SetValue(ClickCommandProperty, value); }
    public ICommand? ModifierClickCommand { get => GetValue(ModifierClickCommandProperty); set => SetValue(ModifierClickCommandProperty, value); }
    public ICommand? CheckCommand { get => GetValue(CheckCommandProperty); set => SetValue(CheckCommandProperty, value); }
}
