using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Snaply.App.Tests;

/// <summary>
/// Documents the Avalonia behaviour that once made the window vanish when opened from the tray after a hidden start:
/// Opened is raised on every Show, so startup work in an Opened handler must be guarded to run once (see App.axaml.cs).
/// </summary>
public class WindowOpenedTests
{
    [AvaloniaFact]
    public void Opened_is_raised_again_each_time_a_hidden_window_is_shown()
    {
        var window = new Window();
        var opened = 0;
        window.Opened += (_, _) => opened++;
        window.Show();
        window.Hide();
        window.Show();
        Assert.Equal(2, opened);
        window.Close();
    }
}
