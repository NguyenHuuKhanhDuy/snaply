using Avalonia;
using Avalonia.Controls;

namespace Snaply.App.Views;

public partial class TagsPage : UserControl
{
    public TagsPage()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != BoundsProperty) return;
            // Narrow windows: slimmer tag list so the gallery beside it stays usable.
            if (this.FindControl<Grid>("TagsGrid") is { } grid && grid.ColumnDefinitions.Count > 0)
                grid.ColumnDefinitions[0].Width = new GridLength(Bounds.Width < 880 ? 180 : 260);
        };
    }
}
