using Avalonia.Controls;

namespace Snaply.App.Views;

public partial class SettingsPage : UserControl
{
    // Shortcut recording is handled by MainWindow (window-level tunnelling), see MainWindow.OnRecordKeyDown.
    public SettingsPage() => InitializeComponent();
}
