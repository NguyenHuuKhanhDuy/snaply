using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snaply.App.ViewModels;

namespace Snaply.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        // Tunnel so a key combination is captured before any focused control (buttons, combo boxes) can react to it.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.IsRecordingShortcut) return;
        e.Handled = true;
        await vm.HandleRecordingKeyAsync(e.Key, e.KeyModifiers);
    }
}
