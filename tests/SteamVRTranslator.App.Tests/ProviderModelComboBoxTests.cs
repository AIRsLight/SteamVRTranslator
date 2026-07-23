using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using SteamVRTranslator.App.Configuration;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class ProviderModelComboBoxTests
{
    [WpfRenderingFact]
    public void TypingFilterDoesNotRestorePreviouslySelectedModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var provider = new TranslationProviderConfiguration
                {
                    Id = "filter-test",
                    Name = "Filter Test",
                    BaseUrl = "https://example.invalid/v1",
                    Model = "vision-alpha"
                };
                var providerList = Assert.IsType<ListBox>(window.FindName("ProviderListBox"));
                var modelComboBox = Assert.IsType<ComboBox>(window.FindName("ProviderModelComboBox"));
                providerList.ItemsSource = new[] { provider };
                providerList.SelectedItem = provider;

                typeof(MainWindow).GetMethod(
                        "BindProviderModels",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(
                        window,
                        [provider, new[] { "vision-alpha", "qwen-vl", "qwen-audio" }, "vision-alpha", false]);

                modelComboBox.ApplyTemplate();
                var editor = Assert.IsType<TextBox>(
                    modelComboBox.Template.FindName("PART_EditableTextBox", modelComboBox));
                editor.SelectAll();
                editor.Text = "qwen";
                editor.CaretIndex = editor.Text.Length;

                Assert.Equal("qwen", editor.Text);
                Assert.Equal(4, editor.CaretIndex);
                Assert.Null(modelComboBox.SelectedItem);
                Assert.Equal(["qwen-audio", "qwen-vl"], modelComboBox.Items.Cast<string>());

                modelComboBox.SelectedItem = "qwen-vl";
                Assert.Equal("qwen-vl", provider.Model);
                Assert.Equal("qwen-vl", modelComboBox.Text);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
