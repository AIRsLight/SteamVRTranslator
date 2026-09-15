using System.Windows.Controls;
using System.Windows.Media;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.SteamVR;

namespace SteamVRTranslator.App;

public partial class MainWindow
{
    private bool _loadingAppearance;

    private void PopulateAppearanceControls()
    {
        _loadingAppearance = true;
        try
        {
            var appearance = _configuration.OverlayAppearance.Normalized();
            SelectByTag(OverlayPresetComboBox, appearance.Preset);
            var displayed = appearance.Preset == "custom" ? appearance : new OverlayAppearanceConfiguration
            {
                Background = ColorText(OverlayTheme.Resolve(OverlayColorRole.Background, OverlayTheme.Parse("#18201D"))),
                Foreground = ColorText(OverlayTheme.Resolve(OverlayColorRole.Text, OverlayTheme.Parse("#F4F8F6"))),
                Accent = ColorText(OverlayTheme.Resolve(OverlayColorRole.Accent, OverlayTheme.Parse("#4DE0C1"))),
                Border = ColorText(OverlayTheme.Resolve(OverlayColorRole.Border, OverlayTheme.Parse("#8FA19C")))
            };
            OverlayBackgroundTextBox.Text = displayed.Background;
            OverlayForegroundTextBox.Text = displayed.Foreground;
            OverlayAccentTextBox.Text = displayed.Accent;
            OverlayBorderTextBox.Text = displayed.Border;
            RefreshAppearanceSwatches();
        }
        finally { _loadingAppearance = false; }
    }

    private void OverlayPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _loadingAppearance) return;
        var appearance = _configuration.OverlayAppearance with { Preset = SelectedTag(OverlayPresetComboBox) };
        ApplyAppearance(appearance);
        PopulateAppearanceControls();
        OverlayColorError.Text = string.Empty;
    }

    private void OverlayColorsApply_Click(object sender, RoutedEventArgs e)
    {
        var appearance = new OverlayAppearanceConfiguration
        {
            Preset = "custom",
            Background = OverlayBackgroundTextBox.Text,
            Foreground = OverlayForegroundTextBox.Text,
            Accent = OverlayAccentTextBox.Text,
            Border = OverlayBorderTextBox.Text
        };
        if (new[] { appearance.Background, appearance.Foreground, appearance.Accent, appearance.Border }
            .Any(value => !OverlayAppearanceConfiguration.IsValidColor(value)))
        {
            OverlayColorError.Text = AppLocalization.Text("Appearance.InvalidColor");
            return;
        }
        ApplyAppearance(appearance.Normalized());
        PopulateAppearanceControls();
        OverlayColorError.Text = string.Empty;
    }

    private void ApplyAppearance(OverlayAppearanceConfiguration appearance)
    {
        _configuration.OverlayAppearance = appearance.Normalized();
        OverlayTheme.Instance.Apply(_configuration.OverlayAppearance);
        SaveConfigurationSafely("叠加层配色");
    }

    private void OverlayColor_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded && !_loadingAppearance) RefreshAppearanceSwatches();
    }

    private void RefreshAppearanceSwatches()
    {
        foreach (var (text, swatch) in new[]
        {
            (OverlayBackgroundTextBox, OverlayBackgroundSwatch),
            (OverlayForegroundTextBox, OverlayForegroundSwatch),
            (OverlayAccentTextBox, OverlayAccentSwatch),
            (OverlayBorderTextBox, OverlayBorderSwatch)
        })
        {
            if (OverlayAppearanceConfiguration.IsValidColor(text.Text))
                swatch.Background = new SolidColorBrush(OverlayTheme.Parse(text.Text.Trim()));
        }
    }

    private void OverlayColorChoose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } || FindName(target) is not TextBox text) return;
        var initial = OverlayAppearanceConfiguration.IsValidColor(text.Text)
            ? OverlayTheme.Parse(text.Text.Trim()) : Colors.White;
        if (NativeColorPicker.Choose(this, initial) is { } chosen)
            text.Text = ColorText(chosen);
    }

    private static string ColorText(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
