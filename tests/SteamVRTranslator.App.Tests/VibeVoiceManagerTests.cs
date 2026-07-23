using System.Net;
using System.Net.Http;
using System.Text;
using SteamVRTranslator.VibeVoice.Manager;
using SteamVRTranslator.VibeVoice.Server;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VibeVoiceManagerTests
{
    [Theory]
    [InlineData(null, InstallationTargets.All)]
    [InlineData("", InstallationTargets.All)]
    [InlineData("unsupported", InstallationTargets.All)]
    [InlineData("runtime", InstallationTargets.Runtime)]
    [InlineData("MODEL", InstallationTargets.Model)]
    public void InstallationTargetNormalizationKeepsBackwardCompatibility(
        string? value,
        string expected)
    {
        Assert.Equal(expected, InstallationTargets.Normalize(value));
    }

    [Fact]
    public void StartupOptionsAcceptRemoteServiceWithoutStartingLocalProcess()
    {
        var options = ManagerStartupOptions.Parse(
        [
            "--service-url",
            "http://192.168.1.20:5090",
            "--api-key",
            "secret",
            "--no-start-service"
        ]);

        Assert.Equal(new Uri("http://192.168.1.20:5090"), options.ServiceUri);
        Assert.Equal("secret", options.ApiKey);
        Assert.False(options.StartLocalService);
    }

    [Theory]
    [InlineData("runtime", "/api/v1/install/runtime")]
    [InlineData("model", "/api/v1/install/model")]
    [InlineData("all", "/api/v1/install")]
    public async Task InstallButtonsUseTheExpectedApiRoute(string target, string expectedPath)
    {
        var handler = new RecordingHandler();
        using var client = new ServiceApiClient(
            new Uri("http://127.0.0.1:5090"),
            "test-key",
            handler);

        await client.InstallAsync(
            target,
            new RuntimeSettingsDto("vulkan", 1, 6, "hf-mirror"),
            CancellationToken.None);

        Assert.Equal(expectedPath, handler.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
        Assert.Contains("\"backend\":\"vulkan\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("\"downloadSource\":\"hf-mirror\"", handler.Body, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent(
                    """
                    {
                      "status": "installing",
                      "runtimeInstalled": false,
                      "modelInstalled": false,
                      "runtimeRunning": false,
                      "backend": "vulkan",
                      "deviceIndex": 1,
                      "threadCount": 6,
                      "downloadSource": "hf-mirror",
                      "currentOperation": "runtime:vulkan",
                      "downloadedBytes": 0,
                      "totalBytes": null,
                      "error": null,
                      "dataDirectory": "data",
                      "modelPath": "model.gguf",
                      "runtimePath": null
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
