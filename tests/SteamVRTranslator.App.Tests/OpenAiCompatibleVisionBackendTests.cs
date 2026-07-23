using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text.Json;
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
                EnableStreaming = true,
                TargetLanguage = "ja-JP",
                SystemPrompt = "NORMAL_SYSTEM",
                MarkdownTranslationPrompt = "NORMAL_TASK"
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
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal("NORMAL_SYSTEM", messages[0].GetProperty("content").GetString());
        var prompt = messages[1].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("NORMAL_TASK", prompt, StringComparison.Ordinal);
        Assert.Contains("目标语言：日语（ja-JP）", prompt, StringComparison.Ordinal);
        Assert.Contains("Target language: Japanese (ja-JP)", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LayoutTranslationKeepsReceivingAnActiveEventStreamUntilHtmlIsComplete()
    {
        var content = new StreamContent(new DelayedChunkStream(
        [
            "data: {\"choices\":[{\"delta\":{\"content\":\"<!doctype html><html>\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\"<body>译文</body></html>\"}}]}\n\n",
            "data: [DONE]\n\n"
        ], TimeSpan.FromMilliseconds(180)));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
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
                EnableStreaming = true,
                TargetLanguage = "en-US",
                LayoutTranslationSystemPrompt = "LAYOUT_SYSTEM"
            },
            client,
            responseIdleTimeout: TimeSpan.FromMilliseconds(400));

        var result = await backend.TranslateLayoutAsync([1, 2, 3], CancellationToken.None);

        Assert.Equal("<!doctype html><html><body>译文</body></html>", result);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        var prompt = payload.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();
        Assert.Contains("完整的单文件 HTML", prompt, StringComparison.Ordinal);
        Assert.Contains("禁止脚本", prompt, StringComparison.Ordinal);
        Assert.Contains("字符视觉高度", prompt, StringComparison.Ordinal);
        Assert.Contains("随视口等比缩放", prompt, StringComparison.Ordinal);
        Assert.Contains("目标语言：英语（en-US）", prompt, StringComparison.Ordinal);
        Assert.Contains("Target language: English (en-US)", prompt, StringComparison.Ordinal);
        Assert.Equal(
            "LAYOUT_SYSTEM",
            payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CustomCommandUsesActiveProviderAndConfiguredSystemPrompt()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StringContent(
                "data: {\"choices\":[{\"delta\":{\"content\":\"答案\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\"内容\"}}]}\n\n" +
                "data: [DONE]\n\n");
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        const string customSystemPrompt = "根据截图执行用户命令。";
        const string customTaskPrompt = "CUSTOM_TASK";
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
                        Model = "vision-model",
                        ApiKey = "test-key"
                    }
                ],
                EnableStreaming = true,
                SystemPrompt = "这条翻译提示词不应被使用。"
            },
            client,
            customSystemPrompt,
            customTaskPrompt);
        var updates = new List<string>();

        var result = await backend.ExecuteCustomCommandAsync(
            [1, 2, 3],
            "解释画面中的谜题",
            Array.Empty<AssistantConversationTurn>(),
            updates.Add,
            CancellationToken.None);

        Assert.Equal("答案内容", result);
        Assert.Equal(["答案", "答案内容"], updates);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(customSystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Contains(
            customTaskPrompt,
            messages[1].GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "解释画面中的谜题",
            messages[1].GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task CustomCommandFollowUpSendsConversationHistoryAndOneImage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"第二个答案\"}}]}",
                System.Text.Encoding.UTF8,
                "application/json")
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
                        Model = "vision-model"
                    }
                ],
                EnableStreaming = false
            },
            client,
            "SYSTEM",
            "CUSTOM_TASK");

        var result = await backend.ExecuteCustomCommandAsync(
            [1, 2, 3],
            "再解释第二个线索",
            [new AssistantConversationTurn("第一个问题", "第一个答案")],
            onPartialResult: null,
            CancellationToken.None);

        Assert.Equal("第二个答案", result);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var firstQuestion = messages[1].GetProperty("content");
        Assert.Contains("第一个问题", firstQuestion[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.StartsWith(
            "data:image/jpeg;base64,",
            firstQuestion[1].GetProperty("image_url").GetProperty("url").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("第一个答案", messages[2].GetProperty("content").GetString());
        Assert.Contains(
            "再解释第二个线索",
            messages[3].GetProperty("content").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            handler.LastRequestBody.Split(
                "data:image/jpeg;base64,",
                StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task TextTranslationUsesSelectedProviderWithoutEmbeddingAnImage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"content\":\"Hello\"}}]}",
                System.Text.Encoding.UTF8,
                "application/json")
        });
        using var client = new HttpClient(handler);
        var configuration = new TranslationConfiguration
        {
            ActiveProviderId = "global",
            Providers =
            [
                new TranslationProviderConfiguration
                {
                    Id = "global",
                    BaseUrl = "https://global.invalid/v1",
                    Model = "global-model"
                },
                new TranslationProviderConfiguration
                {
                    Id = "voice",
                    BaseUrl = "https://voice.invalid/v1",
                    Model = "voice-model"
                }
            ],
            EnableStreaming = false
        };
        var backend = new OpenAiCompatibleVisionBackend(
            configuration,
            client,
            providerId: "voice");

        var result = await backend.TranslateTextAsync(
            "你好",
            "en-US",
            "VOICE_SYSTEM",
            "VOICE_TASK",
            onPartialResult: null,
            CancellationToken.None);

        Assert.Equal("Hello", result);
        Assert.Equal("https://voice.invalid/v1/chat/completions", handler.RequestUri);
        Assert.DoesNotContain("image_url", handler.LastRequestBody, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(handler.LastRequestBody);
        Assert.Equal("voice-model", payload.RootElement.GetProperty("model").GetString());
        var messages = payload.RootElement.GetProperty("messages");
        Assert.Equal("VOICE_SYSTEM", messages[0].GetProperty("content").GetString());
        Assert.Contains("VOICE_TASK", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("目标语言：英语（en-US）", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Target language: English (en-US)", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("你好", messages[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("zh-CN", "目标语言：中文（zh-CN）", "Target language: Chinese (zh-CN)")]
    [InlineData("ja-JP", "目标语言：日语（ja-JP）", "Target language: Japanese (ja-JP)")]
    [InlineData("en-US", "目标语言：英语（en-US）", "Target language: English (en-US)")]
    public void TargetLanguageInstructionIncludesLanguageNameAndCode(
        string language,
        string chineseInstruction,
        string englishInstruction)
    {
        var instruction = OpenAiCompatibleVisionBackend.TargetLanguageInstruction(language);

        Assert.Contains(chineseInstruction, instruction, StringComparison.Ordinal);
        Assert.Contains(englishInstruction, instruction, StringComparison.Ordinal);
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

    private sealed class DelayedChunkStream(
        IReadOnlyList<string> chunks,
        TimeSpan delay) : Stream
    {
        private readonly IReadOnlyList<byte[]> _chunks = chunks
            .Select(chunk => System.Text.Encoding.UTF8.GetBytes(chunk))
            .ToArray();
        private int _chunkIndex;
        private int _chunkOffset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_chunkIndex >= _chunks.Count)
            {
                return 0;
            }

            if (_chunkOffset == 0)
            {
                await Task.Delay(delay, cancellationToken);
            }

            var chunk = _chunks[_chunkIndex];
            var count = Math.Min(buffer.Length, chunk.Length - _chunkOffset);
            chunk.AsMemory(_chunkOffset, count).CopyTo(buffer);
            _chunkOffset += count;
            if (_chunkOffset == chunk.Length)
            {
                _chunkIndex++;
                _chunkOffset = 0;
            }

            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
