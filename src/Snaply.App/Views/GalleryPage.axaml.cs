using Avalonia;
using Avalonia.Controls;
using Snaply.App.ViewModels;

namespace Snaply.App.Views;

public partial class GalleryPage : UserControl
{
    private const double CompactWidth = 880;

    public GalleryPage()
    {
        InitializeComponent();
        var scroller = this.FindControl<ScrollViewer>("Scroller")!;
        scroller.ScrollChanged += (_, _) =>
        {
            // Incremental loading: fetch the next page when within ~1.5 screens of the bottom.
            var remaining = scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height;
            if (remaining < scroller.Viewport.Height * 1.5 && DataContext is GalleryViewModel vm) _ = vm.LoadMoreAsync();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Classes.Set("compact", Bounds.Width < CompactWidth);
        };
    }
}
