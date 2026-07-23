using System.IO;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Speech;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SenseVoiceRuntimeTests
{
    [Fact]
    public void InstallationCheckUsesTheRuntimeForTheSelectedBackend()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sensevoice-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "runtime"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        try
        {
            File.WriteAllText(Path.Combine(root, "runtime", "cpu.exe"), "cpu");
            File.WriteAllText(Path.Combine(root, "models", "model.gguf"), "model");
            File.WriteAllText(Path.Combine(root, "models", "vad.gguf"), "vad");
            var configuration = new SpeechConfiguration
            {
                SenseVoiceExecutablePath = "runtime/cpu.exe",
                SenseVoiceVulkanExecutablePath = "runtime/vulkan.exe",
                SenseVoiceBackend = "cpu",
                SenseVoiceModelPath = "models/model.gguf",
                SenseVoiceVadModelPath = "models/vad.gguf"
            };

            Assert.True(SenseVoiceInstallation.IsConfiguredInstallationAvailable(configuration, root));

            configuration.SenseVoiceBackend = "vulkan";
            Assert.False(SenseVoiceInstallation.IsConfiguredInstallationAvailable(configuration, root));
            File.WriteAllText(Path.Combine(root, "runtime", "vulkan.exe"), "vulkan");
            Assert.True(SenseVoiceInstallation.IsConfiguredInstallationAvailable(configuration, root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void InstallationCheckRejectsMissingAndInvalidPaths()
    {
        var configuration = new SpeechConfiguration
        {
            SenseVoiceExecutablePath = "missing.exe",
            SenseVoiceBackend = "cpu",
            SenseVoiceModelPath = "models/missing.gguf",
            SenseVoiceVadModelPath = null
        };

        Assert.False(SenseVoiceInstallation.IsConfiguredInstallationAvailable(
            configuration,
            Path.GetTempPath()));
        Assert.False(SenseVoiceInstallation.FileExists("bad\0path", Path.GetTempPath()));
    }

    [Theory]
    [InlineData("[sensevoice] 0 vad segments [sensevoice] done 0.01s")]
    [InlineData("no transcription text")]
    [InlineData("No speech detected")]
    public void WorkerClassifiesEmptyRecognitionAsNoSpeech(string diagnostics)
    {
        Assert.True(SenseVoiceResidentWorker.LooksLikeNoSpeech(diagnostics));
    }

    [Fact]
    public void WorkerDoesNotHideAudioReadFailuresAsSilence()
    {
        Assert.False(SenseVoiceResidentWorker.LooksLikeNoSpeech(
            "0 vad segments; read audio failed"));
    }

    [Theory]
    [InlineData("q5_0")]
    [InlineData("q8_0")]
    public void DownloadBundleContainsCpuAndVulkanRuntimes(string variant)
    {
        var assets = SenseVoiceAssetCatalog.ResolveBundle(variant);

        Assert.Contains(assets, asset => asset.Id == "runtime-cpu");
        Assert.Contains(assets, asset => asset.Id == "runtime-vulkan");
        Assert.Contains(assets, asset => asset.Id == "fsmn-vad");
        Assert.Contains(assets, asset => asset.Id == variant);
    }

    [Fact]
    public void VulkanLaunchArgumentsSelectConfiguredGpu()
    {
        var configuration = new SpeechConfiguration
        {
            SenseVoiceBackend = "vulkan",
            SenseVoiceVulkanDeviceIndex = 2,
            EffectiveRecognitionLanguage = "ja"
        };

        var arguments = SenseVoiceCommandTranscriber.BuildArguments(
            configuration,
            "model.gguf",
            "vad.gguf");

        Assert.Equal(
            ["-m", "model.gguf", "--language", "ja", "--backend", "vulkan", "--device", "2", "--vad", "vad.gguf"],
            arguments);
    }

    [Fact]
    public void CpuLaunchArgumentsDoNotEnableVulkan()
    {
        var arguments = SenseVoiceCommandTranscriber.BuildArguments(
            new SpeechConfiguration { SenseVoiceBackend = "cpu" },
            "model.gguf",
            null);

        Assert.Equal(["-m", "model.gguf", "--language", "auto"], arguments);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("zh")]
    [InlineData("en")]
    [InlineData("yue")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void LaunchArgumentsPassEverySupportedRecognitionLanguage(string language)
    {
        var arguments = SenseVoiceCommandTranscriber.BuildArguments(
            new SpeechConfiguration
            {
                SenseVoiceBackend = "cpu",
                EffectiveRecognitionLanguage = language
            },
            "model.gguf",
            null);

        Assert.Equal(language, arguments[3]);
        Assert.Equal("--language", arguments[2]);
    }

    [Theory]
    [InlineData("usage: worker [--language auto|zh|en|yue|ja|ko]", true)]
    [InlineData("usage: worker [--backend cpu|vulkan]", false)]
    [InlineData(null, false)]
    public void RuntimeCapabilityProbeRequiresLanguageArgument(string? helpText, bool expected)
    {
        Assert.Equal(expected, SenseVoiceCommandTranscriber.HelpTextSupportsLanguageSelection(helpText));
    }

    [Fact]
    public void VulkanDeviceEnumerationFallsBackWithoutThrowing()
    {
        var devices = VulkanDeviceInspector.ListDevices();

        Assert.NotNull(devices);
        Assert.All(devices, device => Assert.True(device.Index >= 0));
    }
}
