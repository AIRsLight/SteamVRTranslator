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

    [Fact]
    public void NativePathsStayAsciiInsideAUnicodeInstallDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"语音输入测试-{Guid.NewGuid():N}");
        var modelPath = Path.Combine(root, "models", "sensevoice-small-q8.gguf");
        var audioPath = Path.Combine(root, "runtime-data", "temp", "voice-input.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        File.WriteAllText(modelPath, "model");
        File.WriteAllText(audioPath, "audio");
        try
        {
            var modelArgument = SenseVoiceNativePath.Resolve(modelPath, root);
            var audioArgument = SenseVoiceNativePath.Resolve(audioPath, root);

            Assert.Equal(
                Path.Combine("models", "sensevoice-small-q8.gguf"),
                modelArgument);
            Assert.Equal(
                Path.Combine("runtime-data", "temp", "voice-input.wav"),
                audioArgument);
            Assert.True(SenseVoiceNativePath.IsAscii(modelArgument));
            Assert.True(SenseVoiceNativePath.IsAscii(audioArgument));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ResidentWorkerTranscribesFromAUnicodeInstallDirectory()
    {
        var runtimeSource = Environment.GetEnvironmentVariable(
            "STEAMVR_TRANSLATOR_SENSEVOICE_RUNTIME");
        var modelSource = Environment.GetEnvironmentVariable(
            "STEAMVR_TRANSLATOR_SENSEVOICE_MODEL");
        var audioSource = Environment.GetEnvironmentVariable(
            "STEAMVR_TRANSLATOR_SENSEVOICE_AUDIO");
        if (string.IsNullOrWhiteSpace(runtimeSource) ||
            string.IsNullOrWhiteSpace(modelSource) ||
            string.IsNullOrWhiteSpace(audioSource))
        {
            Assert.NotEqual(
                "1",
                Environment.GetEnvironmentVariable(
                    "STEAMVR_TRANSLATOR_SENSEVOICE_INTEGRATION_REQUIRED"));
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"SteamVR翻译中文路径测试-{Guid.NewGuid():N}");
        var runtimePath = Path.Combine(root, "runtimes", "llama-funasr-sensevoice.exe");
        var modelPath = Path.Combine(root, "models", "sensevoice-small-q5_0.gguf");
        var audioPath = Path.Combine(root, "runtime-data", "temp", "sample.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(runtimePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        File.Copy(runtimeSource, runtimePath);
        File.Copy(modelSource, modelPath);
        File.Copy(audioSource, audioPath);
        try
        {
            var configuration = new SpeechConfiguration
            {
                SenseVoiceBackend = "cpu",
                EffectiveRecognitionLanguage = "auto"
            };
            var arguments = SenseVoiceCommandTranscriber.BuildArguments(
                configuration,
                SenseVoiceNativePath.Resolve(modelPath, root),
                null);

            using var worker = new SenseVoiceResidentWorker(
                runtimePath,
                arguments,
                root);
            var text = await worker.TranscribeAsync(
                audioPath,
                CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(text));
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
