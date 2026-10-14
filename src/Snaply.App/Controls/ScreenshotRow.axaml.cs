using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snaply.App.ViewModels;

namespace Snaply.App.Controls;

/// <summary>Compact list-mode row; clicks report the held modifiers like <see cref="ScreenshotTile"/>.</summary>
public partial class ScreenshotRow : UserControl
{
    public static readonly StyledProperty<ICommand?> ModifierClickCommandProperty = AvaloniaProperty.Register<ScreenshotRow, ICommand?>(nameof(ModifierClickCommand));
    public static readonly StyledProperty<ICommand?> CheckCommandProperty = AvaloniaProperty.Register<ScreenshotRow, ICommand?>(nameof(CheckCommand));

    private KeyModifiers _mods;

    public ScreenshotRow()
    {
        InitializeComponent();
        var root = this.FindControl<Button>("Root")!;
        root.AddHandler(PointerPressedEvent, (_, e) => _mods = e.KeyModifiers, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.Click += (_, _) =>
        {
            if (DataContext is ScreenshotItemViewModel item) ModifierClickCommand?.Execute(new TileClick(item, _mods));
            _mods = KeyModifiers.None;
        };
        this.FindControl<Button>("CheckButton")!.Click += (_, e) =>
        {
            e.Handled = true;
            if (DataContext is ScreenshotItemViewModel item) CheckCommand?.Execute(item);
        };
    }

    public ICommand? ModifierClickCommand { get => GetValue(ModifierClickCommandProperty); set => SetValue(ModifierClickCommandProperty, value); }
    public ICommand? CheckCommand { get => GetValue(CheckCommandProperty); set => SetValue(CheckCommandProperty, value); }
}
