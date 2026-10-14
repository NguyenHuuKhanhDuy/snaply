using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Snaply.App.Controls;

public partial class ScreenshotTile : UserControl
{
    public static readonly StyledProperty<ICommand?> ClickCommandProperty = AvaloniaProperty.Register<ScreenshotTile, ICommand?>(nameof(ClickCommand));

    public ScreenshotTile() => InitializeComponent();

    public ICommand? ClickCommand { get => GetValue(ClickCommandProperty); set => SetValue(ClickCommandProperty, value); }
}
