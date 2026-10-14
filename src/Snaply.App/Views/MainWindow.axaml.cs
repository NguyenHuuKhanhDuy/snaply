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
        // Shortcut recording is caught at window level (tunnelling): the focused control disappears when recording starts,
        // so a handler on the settings page would never see the key.
        AddHandler(KeyDownEvent, OnRecordKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnRecordKeyUp, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private SettingsViewModel? RecordingVm =>
        DataContext is MainViewModel { CurrentPage: SettingsViewModel { IsRecordingShortcut: true } vm } ? vm : null;

    private async void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        if (RecordingVm is not { } vm) return;
        e.Handled = true;
        await vm.HandleRecordingKeyAsync(e.Key, e.KeyModifiers);
    }

    // Windows reports Print Screen only on key release.
    private async void OnRecordKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.PrintScreen || RecordingVm is not { } vm) return;
        e.Handled = true;
        await vm.HandleRecordingKeyAsync(e.Key, e.KeyModifiers);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control) && DataContext is MainViewModel main)
        {
            e.Handled = true;
            _ = main.FocusSearchAsync();
            return;
        }

        // Escape closes an open dialog (as Cancel).
        if (e.Key == Key.Escape && DataContext is MainViewModel { Dialogs.Current: not null } vm)
        {
            vm.Dialogs.CancelCurrent();
            e.Handled = true;
        }
    }
}
