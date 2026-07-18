using System.Net;
using System.Net.Http;
using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class OpenAiCompatibleProviderClientTests
{
    [Fact]
    public async Task ModelsEndpointUsesBearerAuthenticationAndReturnsSortedDistinctIds()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"object\":\"list\",\"data\":[{\"id\":\"z-model\"},{\"id\":\"a-model\"},{\"id\":\"A-MODEL\"}]}")
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiCompatibleProviderClient(client);

        var models = await provider.GetModelsAsync(
            "https://example.invalid/v1/chat/completions",
            "secret",
            CancellationToken.None);

        Assert.Equal(["a-model", "z-model"], models);
        Assert.Equal("https://example.invalid/v1/models", handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task EmptyApiKeySupportsLocalCompatibleServers()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"local-model\"}]}")
        });
        using var client = new HttpClient(handler);

        var models = await new OpenAiCompatibleProviderClient(client)
            .GetModelsAsync("http://127.0.0.1:1234/v1", string.Empty, CancellationToken.None);

        Assert.Equal(["local-model"], models);
        Assert.Null(handler.AuthorizationScheme);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(response);
        }
    }
}
