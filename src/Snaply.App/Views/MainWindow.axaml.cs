using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Snaply.App.Services;
using Snaply.App.ViewModels;

namespace Snaply.App.Views;

public partial class MainWindow : Window
{
    private const double CompactWidth = 900;

    public MainWindow()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty && DataContext is MainViewModel vm) vm.SetCompact(Bounds.Width < CompactWidth);
        };
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Escape closes an open dialog (as Cancel).
        if (e.Key == Key.Escape && DataContext is MainViewModel { Dialogs.Current: not null } vm)
        {
            vm.Dialogs.CancelCurrent();
            e.Handled = true;
        }
    }
}
