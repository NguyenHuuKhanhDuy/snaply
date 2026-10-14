using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Snaply.App.Views;

/// <summary>Minimal startup-failure dialog; deliberately built in code so it works before any resources or services exist.</summary>
public sealed class FatalErrorWindow : Window
{
    public FatalErrorWindow(string title, string message)
    {
        Title = "Snaply";
        Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 8) };
        close.Click += (_, _) => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(1);
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                close,
            },
        };
    }
}
