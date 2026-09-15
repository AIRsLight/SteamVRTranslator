using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.SteamVR;

public enum OverlayColorRole
{
    Background, Surface, Text, MutedText, Accent, AccentHover, AccentSoft, OnAccent, Border, MutedBorder, Danger, Warning
}

/// <summary>Shared palette for WPF windows and textures; classic colors retain their original alpha.</summary>
public sealed class OverlayTheme : INotifyPropertyChanged
{
    public static OverlayTheme Instance { get; } = new();
    private OverlayAppearanceConfiguration _appearance = new();
    private long _revision;
    public long Revision => Interlocked.Read(ref _revision);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(OverlayAppearanceConfiguration appearance)
    {
        var normalized = appearance.Normalized();
        if (_appearance == normalized) return;
        Volatile.Write(ref _appearance, normalized);
        Interlocked.Increment(ref _revision);
        PropertyChanged?.Invoke(this, new(nameof(Revision)));
    }

    public static Color Resolve(OverlayColorRole role, Color classic)
    {
        var appearance = Volatile.Read(ref Instance._appearance);
        if (appearance.Preset == "classic") return classic;
        var custom = appearance.Preset == "custom";
        var monochrome = appearance.Preset == "monochrome";
        var background = Parse(custom ? appearance.Background : monochrome ? "#121212" : "#F4F7FC");
        var foreground = Parse(custom ? appearance.Foreground : monochrome ? "#F5F5F5" : "#202B3D");
        var accent = Parse(custom ? appearance.Accent : monochrome ? "#E5E5E5" : "#3568BD");
        var border = Parse(custom ? appearance.Border : monochrome ? "#8A8A8A" : "#8999B0");
        var color = role switch
        {
            OverlayColorRole.Background => background,
            OverlayColorRole.Surface => Mix(background, foreground, 0.08),
            OverlayColorRole.Text => foreground,
            OverlayColorRole.MutedText => Mix(background, foreground, 0.70),
            OverlayColorRole.Accent => accent,
            OverlayColorRole.AccentHover => Mix(accent, foreground, 0.3),
            OverlayColorRole.AccentSoft => Mix(background, accent, 0.25),
            OverlayColorRole.OnAccent => monochrome ? background : accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114 > 160
                ? Parse("#172033") : Colors.White,
            OverlayColorRole.Border => border,
            OverlayColorRole.MutedBorder => Mix(background, border, 0.65),
            OverlayColorRole.Danger => monochrome ? foreground : Parse(custom ? "#E26363" : "#B32635"),
            OverlayColorRole.Warning => monochrome ? accent : Parse(custom ? "#D89F43" : "#805B18"),
            _ => foreground
        };
        color.A = classic.A;
        return color;
    }

    public static Brush Brush(OverlayColorRole role, string classic)
    {
        var brush = new SolidColorBrush(Resolve(role, Parse(classic)));
        brush.Freeze();
        return brush;
    }

    public static Binding CreateBinding(OverlayColorRole role, string classic) => new(nameof(Revision))
    {
        Source = Instance,
        Mode = BindingMode.OneWay,
        Converter = new ThemeBrushConverter(role, classic)
    };

    public static Brush LiveBrush(OverlayColorRole role, string classic)
    {
        var brush = new SolidColorBrush();
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty, CreateBinding(role, classic));
        return brush;
    }

    public static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color);
    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private sealed class ThemeBrushConverter(OverlayColorRole role, string classic) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            targetType == typeof(Color) ? Resolve(role, Parse(classic)) : Brush(role, classic);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

[MarkupExtensionReturnType(typeof(Color))]
public sealed class OverlayColorExtension : MarkupExtension
{
    public OverlayColorRole Role { get; set; }
    public string Classic { get; set; } = "#FFFFFF";
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        OverlayTheme.CreateBinding(Role, Classic).ProvideValue(serviceProvider);
}

[MarkupExtensionReturnType(typeof(Brush))]
public sealed class OverlayBrushExtension : MarkupExtension
{
    public OverlayColorRole Role { get; set; }
    public string Classic { get; set; } = "#FFFFFF";
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        OverlayTheme.CreateBinding(Role, Classic).ProvideValue(serviceProvider);
}
