using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Translation;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class GoogleAiStudioVisionBackendTests
{
    [Fact]
    public void BackendFactorySelectsGoogleNativeImplementation()
    {
        var configuration = Configuration(
            "gemini-flash-latest",
            streaming: true,
            new PromptGenerationConfiguration());
        var provider = configuration.GetActiveProvider();
        using var client = new HttpClient();

        var backend = TranslationBackendFactory.Create(
            configuration,
            Assert.IsType<TranslationProviderConfiguration>(provider),
            client);

        Assert.IsType<GoogleAiStudioVisionBackend>(backend);
    }

    [Fact]
    public async Task NativeRequestUsesSystemInstructionInlineImageAndThinkingBudget()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "candidates": [{
                    "content": {
                      "parts": [
                        {"thought": true, "text": "private reasoning"},
                        {"text": "译文"}
                      ]
                    }
                  }]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var backend = new GoogleAiStudioVisionBackend(
            Configuration(
                "gemini-2.5-flash",
                streaming: false,
                new PromptGenerationConfiguration
                {
                    EnableThinking = false,
                    Temperature = 0.25,
                    TopP = 0.8,
                    MaximumOutputTokens = 2048
                }),
            client);

        var result = await backend.TranslateAsync(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            null,
            CancellationToken.None);

        Assert.Equal("译文", result);
        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/" +
            "gemini-2.5-flash:generateContent",
            handler.RequestUri);
        Assert.Equal("google-key", handler.ApiKey);
        Assert.Null(handler.AuthorizationScheme);

        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        var root = payload.RootElement;
        Assert.Equal(
            "SYSTEM",
            root.GetProperty("systemInstruction")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString());
        var user = root.GetProperty("contents")[0];
        Assert.Equal("user", user.GetProperty("role").GetString());
        Assert.Contains(
            "目标语言：中文（zh-CN）",
            user.GetProperty("parts")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        var inlineData = user.GetProperty("parts")[1].GetProperty("inlineData");
        Assert.Equal("image/png", inlineData.GetProperty("mimeType").GetString());
        Assert.NotEmpty(inlineData.GetProperty("data").GetString() ?? string.Empty);

        var generation = root.GetProperty("generationConfig");
        Assert.Equal(0.25, generation.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.8, generation.GetProperty("topP").GetDouble(), 3);
        Assert.Equal(2048, generation.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(
            0,
            generation.GetProperty("thinkingConfig")
                .GetProperty("thinkingBudget")
                .GetInt32());
    }

    [Fact]
    public async Task EventStreamPublishesTextAndOmitsDeprecatedSamplingParameters()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StringContent(
                "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"你\"}]}}]}\n\n" +
                "data: {\"candidates\":[{\"content\":{\"parts\":[{\"thought\":true,\"text\":\"ignore\"},{\"text\":\"好\"}]}}]}\n\n");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        var backend = new GoogleAiStudioVisionBackend(
            Configuration(
                "gemini-3.6-flash",
                streaming: true,
                new PromptGenerationConfiguration
                {
                    EnableThinking = false,
                    Temperature = 1,
                    TopP = 0.5
                }),
            client);
        var updates = new List<string>();

        var result = await backend.TranslateAsync(
            [0xFF, 0xD8, 0xFF],
            updates.Add,
            CancellationToken.None);

        Assert.Equal("你好", result);
        Assert.Equal(["你", "你好"], updates);
        Assert.EndsWith(
            "gemini-3.6-flash:streamGenerateContent?alt=sse",
            handler.RequestUri,
            StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        var generation = payload.RootElement.GetProperty("generationConfig");
        Assert.False(generation.TryGetProperty("temperature", out _));
        Assert.False(generation.TryGetProperty("topP", out _));
        Assert.Equal(
            "minimal",
            generation.GetProperty("thinkingConfig")
                .GetProperty("thinkingLevel")
                .GetString());
    }

    [Fact]
    public async Task TextTranslationUsesNativeTextContentWithoutInlineImage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"こんにちは\"}]}}]}",
                Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var backend = new GoogleAiStudioVisionBackend(
            Configuration(
                "gemini-2.5-flash",
                streaming: false,
                new PromptGenerationConfiguration()),
            client);

        var result = await backend.TranslateTextAsync(
            "hello",
            "ja-JP",
            "TEXT_SYSTEM",
            "TEXT_TASK",
            null,
            CancellationToken.None);

        Assert.Equal("こんにちは", result);
        Assert.DoesNotContain("inlineData", handler.LastRequestBody, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal(
            "TEXT_SYSTEM",
            payload.RootElement.GetProperty("systemInstruction")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString());
        var prompt = payload.RootElement.GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("TEXT_TASK", prompt, StringComparison.Ordinal);
        Assert.Contains("Source ASR text:\nhello", prompt, StringComparison.Ordinal);
    }

    private static TranslationConfiguration Configuration(
        string model,
        bool streaming,
        PromptGenerationConfiguration directGeneration) =>
        new()
        {
            ActiveProviderId = "google",
            Providers =
            [
                new TranslationProviderConfiguration
                {
                    Id = "google",
                    Type = TranslationProviderConfiguration.GoogleAiStudioType,
                    BaseUrl = GoogleAiStudioProviderClient.DefaultBaseUrl,
                    ApiKey = "google-key",
                    Model = model
                }
            ],
            TargetLanguage = "zh-CN",
            EnableStreaming = streaming,
            SystemPrompt = "SYSTEM",
            MarkdownTranslationPrompt = "TASK",
            PromptGeneration = new PromptGenerationSettingsConfiguration
            {
                DirectTranslation = directGeneration
            }
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        public string? RequestUri { get; private set; }

        public string? ApiKey { get; private set; }

        public string? AuthorizationScheme { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.AbsoluteUri;
            ApiKey = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.Single()
                : null;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return responseFactory(request);
        }
    }
}
