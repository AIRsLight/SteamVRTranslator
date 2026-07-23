using System.IO;
using SteamVRTranslator.VibeVoice.Server;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VibeVoiceServiceOptionsTests
{
    [Fact]
    public void DefaultDataDirectoryLivesBesideServiceBinary()
    {
        var path = new ServiceOptions().ResolveDataDirectory();

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "data"),
            path,
            ignoreCase: true);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5090")]
    [InlineData("http://localhost:5090")]
    [InlineData("http://[::1]:5090")]
    public void LoopbackBindingDoesNotRequireApiKey(string listenUrl)
    {
        var options = new ServiceOptions { ListenUrl = listenUrl };

        options.Normalize();
        options.ValidateRemoteBinding();
    }

    [Theory]
    [InlineData("http://0.0.0.0:5090")]
    [InlineData("http://192.168.1.20:5090")]
    [InlineData("http://*:5090")]
    public void RemoteBindingWithoutApiKeyIsRejected(string listenUrl)
    {
        var options = new ServiceOptions { ListenUrl = listenUrl };

        options.Normalize();

        Assert.Throws<InvalidOperationException>(options.ValidateRemoteBinding);
    }

    [Fact]
    public void RemoteBindingWithApiKeyIsAllowed()
    {
        var options = new ServiceOptions
        {
            ListenUrl = "http://0.0.0.0:5090",
            ApiKey = "test-secret"
        };

        options.Normalize();
        options.ValidateRemoteBinding();
    }

    [Fact]
    public void RuntimeSettingsNormalizeUnsupportedValues()
    {
        var settings = new VibeVoiceRuntimeSettings
        {
            Backend = "unsupported",
            DeviceIndex = -2,
            ThreadCount = int.MaxValue,
            DownloadSource = "unknown"
        };

        settings.Normalize();

        Assert.Equal("cpu", settings.Backend);
        Assert.Equal(0, settings.DeviceIndex);
        Assert.InRange(settings.ThreadCount, 1, Math.Max(1, Environment.ProcessorCount));
        Assert.Equal("official", settings.DownloadSource);
    }
}
