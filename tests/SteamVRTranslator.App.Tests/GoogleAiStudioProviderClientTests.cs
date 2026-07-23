using System.Net;
using System.Net.Http;
using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class GoogleAiStudioProviderClientTests
{
    [Fact]
    public async Task ModelsEndpointUsesGoogleApiKeyFiltersAndPaginates()
    {
        var handler = new StubHandler(request =>
        {
            var secondPage = request.RequestUri?.Query.Contains(
                "pageToken=next%2Bpage",
                StringComparison.Ordinal) == true;
            var json = secondPage
                ? """
                  {
                    "models": [
                      {
                        "name": "models/gemini-z",
                        "supportedGenerationMethods": ["generateContent"]
                      }
                    ]
                  }
                  """
                : """
                  {
                    "models": [
                      {
                        "name": "models/gemini-a",
                        "supportedGenerationMethods": ["generateContent"]
                      },
                      {
                        "name": "models/gemini-flash-latest",
                        "supportedGenerationMethods": ["generateContent"]
                      },
                      {
                        "name": "models/embedding-001",
                        "supportedGenerationMethods": ["embedContent"]
                      }
                    ],
                    "nextPageToken": "next+page"
                  }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            };
        });
        using var client = new HttpClient(handler);

        var models = await new GoogleAiStudioProviderClient(client).GetModelsAsync(
            GoogleAiStudioProviderClient.DefaultBaseUrl,
            "google-key",
            CancellationToken.None);

        Assert.Equal(["gemini-flash-latest", "gemini-z", "gemini-a"], models);
        Assert.Equal(2, handler.RequestUris.Count);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000",
            handler.RequestUris[0]);
        Assert.Contains("pageToken=next%2Bpage", handler.RequestUris[1]);
        Assert.All(handler.ApiKeys, key => Assert.Equal("google-key", key));
        Assert.All(handler.AuthorizationSchemes, scheme => Assert.Null(scheme));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<string> RequestUris { get; } = [];

        public List<string?> ApiKeys { get; } = [];

        public List<string?> AuthorizationSchemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
            ApiKeys.Add(request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.Single()
                : null);
            AuthorizationSchemes.Add(request.Headers.Authorization?.Scheme);
            return Task.FromResult(responseFactory(request));
        }
    }
}
