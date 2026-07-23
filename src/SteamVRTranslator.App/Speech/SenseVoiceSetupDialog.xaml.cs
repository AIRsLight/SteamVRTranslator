namespace SteamVRTranslator.App.Speech;

public partial class SenseVoiceSetupDialog : Window
{
    public SenseVoiceSetupDialog(
        string modelDisplayName,
        IEnumerable<string> missingAssets,
        bool useMirror)
    {
        InitializeComponent();
        ModelValueText.Text = modelDisplayName;
        MissingAssetsText.Text = string.Join(
            Localization.AppLocalization.Instance.Language == Localization.ApplicationLanguages.English
                ? ", "
                : "、",
            missingAssets.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        MirrorSourceRadioButton.IsChecked = useMirror;
        OfficialSourceRadioButton.IsChecked = !useMirror;
    }

    public bool UseMirror => MirrorSourceRadioButton.IsChecked == true;

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
