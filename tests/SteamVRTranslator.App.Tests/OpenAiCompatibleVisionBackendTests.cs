using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class OpenAiCompatibleVisionBackendTests
{
    [Fact]
    public async Task EventStreamPublishesIncrementalText()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n" +
                "data: [DONE]\n\n");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var backend = new OpenAiCompatibleVisionBackend(
            new TranslationConfiguration
            {
                ActiveProviderId = "test",
                Providers =
                [
                    new TranslationProviderConfiguration
                    {
                        Id = "test",
                        BaseUrl = "https://example.invalid/v1",
                        Model = "qwen3-vl-flash",
                        ApiKey = "test-key"
                    }
                ],
                EnableStreaming = true
            },
            client);
        var updates = new List<string>();

        var result = await backend.TranslateAsync([1, 2, 3], updates.Add, CancellationToken.None);

        Assert.Equal("你好", result);
        Assert.Equal(["你", "你好"], updates);
        Assert.Contains("\"stream\":true", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"enable_thinking\":false", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"system\"", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("data:image/jpeg;base64,", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-key", handler.AuthorizationParameter);
        Assert.Equal("https://example.invalid/v1/chat/completions", handler.RequestUri);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        public string? AuthorizationScheme { get; private set; }

        public string? AuthorizationParameter { get; private set; }

        public string? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri?.AbsoluteUri;
            return responseFactory(request);
        }
    }
}
