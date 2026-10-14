using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Snaply.App.Controls;

/// <summary>Centered illustration + message shown when a list or page has no content.</summary>
public class EmptyState : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Description));
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<EmptyState, Geometry?>(nameof(Icon));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
}

/// <summary>A label/description on the left and an editor control on the right, used throughout Settings.</summary>
public class SettingsRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingsRow, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SettingsRow, string?>(nameof(Description));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Titled settings section card.</summary>
public class SectionCard : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SectionCard, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DescriptionProperty = AvaloniaProperty.Register<SectionCard, string?>(nameof(Description));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
}

/// <summary>Displays a shortcut such as "Alt+Shift+S" as individual keycaps.</summary>
public class KeyCaps : TemplatedControl
{
    public static readonly StyledProperty<string?> GestureProperty = AvaloniaProperty.Register<KeyCaps, string?>(nameof(Gesture));
    public static readonly DirectProperty<KeyCaps, IReadOnlyList<string>> PartsProperty =
        AvaloniaProperty.RegisterDirect<KeyCaps, IReadOnlyList<string>>(nameof(Parts), o => o.Parts);

    private IReadOnlyList<string> _parts = Array.Empty<string>();

    public string? Gesture { get => GetValue(GestureProperty); set => SetValue(GestureProperty, value); }
    public IReadOnlyList<string> Parts { get => _parts; private set => SetAndRaise(PartsProperty, ref _parts, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GestureProperty)
            Parts = string.IsNullOrWhiteSpace(Gesture) ? Array.Empty<string>() : Gesture!.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
