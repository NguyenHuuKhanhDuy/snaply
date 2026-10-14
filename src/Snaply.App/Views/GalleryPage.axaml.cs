using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Snaply.App.Controls;
using Snaply.App.ViewModels;

namespace Snaply.App.Views;

public partial class GalleryPage : UserControl
{
    private const double CompactWidth = 880;
    private const double TileSlot = 220 + 14;

    private GalleryViewModel? _vm;

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
            if (e.Property != BoundsProperty) return;
            var compact = Bounds.Width < CompactWidth;
            Classes.Set("compact", compact);
            // Compact: the details panel floats over the grid instead of taking a column, so search and filters keep their width.
            if (this.FindControl<Border>("DetailsHost") is { } host)
            {
                Grid.SetColumn(host, compact ? 0 : 1);
                Grid.SetColumnSpan(host, compact ? 2 : 1);
                host.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Stretch;
                host.Width = compact ? Math.Min(352, Math.Max(280, Bounds.Width - 64)) : 352;
            }
        };

        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.FocusMoved -= ScrollIntoView;
            _vm = DataContext as GalleryViewModel;
            if (_vm is not null) _vm.FocusMoved += ScrollIntoView;
        };
        KeyDown += OnKeyDown;
    }

    private void ScrollIntoView(ScreenshotItemViewModel item)
    {
        foreach (var control in this.FindControl<ScrollViewer>("Scroller")!.GetVisualDescendants().OfType<Control>())
        {
            if (control is ScreenshotTile or ScreenshotRow && ReferenceEquals(control.DataContext, item) && control.IsVisible && control.IsEffectivelyVisible)
            {
                control.BringIntoView();
                return;
            }
        }
    }

    private int Columns()
    {
        var scroller = this.FindControl<ScrollViewer>("Scroller")!;
        return _vm?.IsListMode == true ? 1 : Math.Max(1, (int)(scroller.Viewport.Width / TileSlot));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Typing in the search box (or any text field) keeps its normal behaviour.
        if (_vm is null || e.Handled || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var list = _vm.IsListMode;
        switch (e.Key)
        {
            case Key.Left when !list: _vm.MoveFocusBy(-1); break;
            case Key.Right when !list: _vm.MoveFocusBy(1); break;
            case Key.Up: if (list) _vm.MoveFocusBy(-1); else _vm.MoveFocusVertical(Columns(), -1); break;
            case Key.Down: if (list) _vm.MoveFocusBy(1); else _vm.MoveFocusVertical(Columns(), 1); break;
            case Key.Home: _vm.MoveFocusTo(_vm.Items.FirstOrDefault()); break;
            case Key.End: _vm.MoveFocusTo(_vm.Items.LastOrDefault()); break;
            case Key.Space: _vm.ToggleFocusedCheck(); break;
            case Key.Enter: _vm.OpenFocused(); break;
            case Key.Delete: if (_vm.BulkDeleteCommand.CanExecute(null)) _vm.BulkDeleteCommand.Execute(null); break;
            case Key.A when ctrl: _vm.SelectAll(); break;
            case Key.Escape:
                if (_vm.HasChecked) _vm.ClearSelection();
                else if (_vm.Selected is not null) _vm.Selected = null;
                else return;
                break;
            default: return;
        }
        e.Handled = true;
    }
}
