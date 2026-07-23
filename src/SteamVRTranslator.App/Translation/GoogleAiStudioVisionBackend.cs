using System.Text;
using System.Text.Json;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Translation;

public sealed class GoogleAiStudioVisionBackend : ITranslationBackend
{
    internal static readonly TimeSpan DefaultResponseIdleTimeout = TimeSpan.FromSeconds(90);

    private readonly TranslationConfiguration _configuration;
    private readonly TranslationProviderConfiguration _provider;
    private readonly HttpClient _httpClient;
    private readonly string _customCommandSystemPrompt;
    private readonly string _customCommandPrompt;
    private readonly TimeSpan _responseIdleTimeout;
    private readonly PromptProviderPurpose _textTranslationPurpose;

    public GoogleAiStudioVisionBackend(
        TranslationConfiguration configuration,
        HttpClient httpClient,
        string? customCommandSystemPrompt = null,
        string? customCommandPrompt = null,
        TimeSpan? responseIdleTimeout = null,
        string? providerId = null,
        PromptProviderPurpose textTranslationPurpose = PromptProviderPurpose.VoiceTranslation)
    {
        _configuration = configuration;
        var selectedProviderId = string.IsNullOrWhiteSpace(providerId)
            ? configuration.ActiveProviderId
            : providerId;
        _provider = configuration.Providers.FirstOrDefault(provider =>
                        string.Equals(provider.Id, selectedProviderId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("当前启用的模型 Provider 不存在。");
        if (!_provider.IsGoogleAiStudio)
        {
            throw new InvalidOperationException(
                "当前启用的 Provider 不是 Google AI Studio 原生提供商。");
        }

        _httpClient = httpClient;
        _customCommandSystemPrompt = customCommandSystemPrompt is null
            ? configuration.SystemPrompt
            : customCommandSystemPrompt.Trim();
        _customCommandPrompt = customCommandPrompt ?? BuiltInPromptDefaults.CustomCommandPrompt;
        _responseIdleTimeout = responseIdleTimeout ?? DefaultResponseIdleTimeout;
        _textTranslationPurpose = textTranslationPurpose;
        if (_responseIdleTimeout <= TimeSpan.Zero && _responseIdleTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(responseIdleTimeout),
                "响应空闲超时必须大于零或为无限。");
        }
    }

    public Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        var prompt = ComposePrompt(
            _configuration.MarkdownTranslationPrompt,
            OpenAiCompatibleVisionBackend.TargetLanguageInstruction(
                _configuration.TargetLanguage));
        return SendAsync(
            BuildVisionContents(imageBytes, prompt),
            _configuration.SystemPrompt,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(PromptProviderPurpose.DirectTranslation),
            onPartialResult,
            cancellationToken);
    }

    public Task<string?> TranslateLayoutAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        var prompt = ComposePrompt(
            _configuration.LayoutTranslationPrompt,
            OpenAiCompatibleVisionBackend.TargetLanguageInstruction(
                _configuration.TargetLanguage));
        return SendAsync(
            BuildVisionContents(imageBytes, prompt),
            _configuration.LayoutTranslationSystemPrompt,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(PromptProviderPurpose.LayoutTranslation),
            onPartialResult: null,
            cancellationToken);
    }

    public Task<string?> ExecuteCustomCommandAsync(
        byte[] imageBytes,
        string command,
        IReadOnlyList<AssistantConversationTurn> history,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("自定义命令为空。");
        }

        var prompt = ComposePrompt(
            _customCommandPrompt,
            "用户命令：\n" + command.Trim());
        return SendAsync(
            BuildCustomCommandContents(
                imageBytes,
                prompt,
                history ?? Array.Empty<AssistantConversationTurn>()),
            _customCommandSystemPrompt,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(PromptProviderPurpose.CustomCommand),
            onPartialResult,
            cancellationToken);
    }

    public Task<string?> TranslateTextAsync(
        string sourceText,
        string targetLanguage,
        string systemPrompt,
        string taskPrompt,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new InvalidOperationException("语音转写文本为空。");
        }

        var prompt = ComposePrompt(
            taskPrompt,
            $"{OpenAiCompatibleVisionBackend.TargetLanguageInstruction(targetLanguage)}" +
            $"\n\nSource ASR text:\n{sourceText.Trim()}");
        return SendAsync(
            [BuildContent("user", [TextPart(prompt)])],
            systemPrompt,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(_textTranslationPurpose),
            onPartialResult,
            cancellationToken);
    }

    private async Task<string?> SendAsync(
        IReadOnlyList<object> contents,
        string systemPrompt,
        bool stream,
        PromptGenerationConfiguration generation,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        var endpoint = GoogleAiStudioProviderClient.ResolveGenerateEndpoint(
            _provider.BaseUrl,
            _provider.Model,
            stream);
        var payload = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["generationConfig"] = BuildGenerationConfig(generation)
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["systemInstruction"] = new
            {
                parts = new[] { new { text = systemPrompt.Trim() } }
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        GoogleAiStudioProviderClient.ApplyApiKey(request, _provider.ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using var response = await WithIdleTimeoutAsync(
            token => _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await ReadContentAsync(response.Content, cancellationToken);
            throw new InvalidOperationException(
                $"Google AI Studio 返回 HTTP {(int)response.StatusCode}: " +
                Limit(errorText, 500));
        }

        if (stream &&
            string.Equals(
                response.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return await ReadEventStreamAsync(response, onPartialResult, cancellationToken);
        }

        var responseText = await ReadContentAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(responseText);
        var result = ExtractCandidateText(document.RootElement)?.Trim();
        if (!string.IsNullOrEmpty(result))
        {
            onPartialResult?.Invoke(result);
        }

        return result;
    }

    private Dictionary<string, object?> BuildGenerationConfig(
        PromptGenerationConfiguration generation)
    {
        var config = new Dictionary<string, object?>();
        if (!UsesDeprecatedSamplingParameters(_provider.Model))
        {
            config["temperature"] = generation.Temperature;
            config["topP"] = generation.TopP;
        }
        if (generation.MaximumOutputTokens > 0)
        {
            config["maxOutputTokens"] = generation.MaximumOutputTokens;
        }

        var thinkingConfig = BuildThinkingConfig(_provider.Model, generation.EnableThinking);
        if (thinkingConfig is not null)
        {
            config["thinkingConfig"] = thinkingConfig;
        }

        return config;
    }

    private static object? BuildThinkingConfig(string model, bool enableThinking)
    {
        if (enableThinking)
        {
            return null;
        }

        var normalized = GoogleAiStudioProviderClient.NormalizeModelName(model);
        if (normalized.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) ||
            IsLatestGeminiAlias(normalized))
        {
            return new { thinkingLevel = "minimal" };
        }
        if (normalized.Contains("gemini-2.5-flash", StringComparison.OrdinalIgnoreCase))
        {
            return new { thinkingBudget = 0 };
        }

        return null;
    }

    private static bool UsesDeprecatedSamplingParameters(string model)
    {
        var normalized = GoogleAiStudioProviderClient.NormalizeModelName(model);
        return normalized.StartsWith("gemini-3.5-flash-lite", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("gemini-3.6-", StringComparison.OrdinalIgnoreCase) ||
               IsLatestGeminiAlias(normalized);
    }

    private static bool IsLatestGeminiAlias(string model) =>
        string.Equals(model, "gemini-flash-latest", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(model, "gemini-flash-lite-latest", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(model, "gemini-pro-latest", StringComparison.OrdinalIgnoreCase);

    private async Task<string?> ReadEventStreamAsync(
        HttpResponseMessage response,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        await using var stream = await WithIdleTimeoutAsync(
            token => response.Content.ReadAsStreamAsync(token),
            cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        while (true)
        {
            var line = await WithIdleTimeoutAsync(
                token => reader.ReadLineAsync(token).AsTask(),
                cancellationToken);
            if (line is null)
            {
                break;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var fragment = ExtractCandidateText(document.RootElement);
            if (string.IsNullOrEmpty(fragment))
            {
                continue;
            }

            result.Append(fragment);
            onPartialResult?.Invoke(result.ToString());
        }

        return result.ToString().Trim();
    }

    private async Task<string> ReadContentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await WithIdleTimeoutAsync(
            token => content.ReadAsStreamAsync(token),
            cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await WithIdleTimeoutAsync(
                token => reader.ReadAsync(buffer.AsMemory(), token).AsTask(),
                cancellationToken);
            if (count == 0)
            {
                return result.ToString();
            }

            result.Append(buffer, 0, count);
        }
    }

    private async Task<T> WithIdleTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (_responseIdleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await operation(cancellationToken);
        }

        using var idleCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCancellation.CancelAfter(_responseIdleTimeout);
        try
        {
            return await operation(idleCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Google AI Studio 连续 {_responseIdleTimeout.TotalSeconds:F0} 秒未返回新数据。",
                exception);
        }
    }

    private static string? ExtractCandidateText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return null;
        }

        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("thought", out var thought) &&
                thought.ValueKind == JsonValueKind.True)
            {
                continue;
            }
            if (part.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                result.Append(text.GetString());
            }
        }

        return result.Length == 0 ? null : result.ToString();
    }

    private static IReadOnlyList<object> BuildVisionContents(
        byte[] imageBytes,
        string prompt) =>
        [BuildContent("user", BuildUserParts(imageBytes, prompt))];

    private IReadOnlyList<object> BuildCustomCommandContents(
        byte[] imageBytes,
        string prompt,
        IReadOnlyList<AssistantConversationTurn> history)
    {
        var contents = new List<object>();
        if (history.Count == 0)
        {
            contents.Add(BuildContent("user", BuildUserParts(imageBytes, prompt)));
            return contents;
        }

        var firstTurn = history[0];
        contents.Add(BuildContent(
            "user",
            BuildUserParts(
                imageBytes,
                ComposePrompt(
                    _customCommandPrompt,
                    "用户命令：\n" + firstTurn.User))));
        contents.Add(BuildContent("model", [TextPart(firstTurn.Assistant)]));
        foreach (var turn in history.Skip(1))
        {
            contents.Add(BuildContent("user", [TextPart(turn.User)]));
            contents.Add(BuildContent("model", [TextPart(turn.Assistant)]));
        }
        contents.Add(BuildContent("user", [TextPart(prompt)]));
        return contents;
    }

    private static IReadOnlyList<object> BuildUserParts(byte[] imageBytes, string prompt)
    {
        var parts = new List<object> { TextPart(prompt) };
        if (imageBytes.Length > 0)
        {
            parts.Add(new
            {
                inlineData = new
                {
                    mimeType = DetectImageMimeType(imageBytes),
                    data = Convert.ToBase64String(imageBytes)
                }
            });
        }

        return parts;
    }

    private static object BuildContent(string role, IReadOnlyList<object> parts) =>
        new { role, parts };

    private static object TextPart(string text) => new { text };

    private static string DetectImageMimeType(byte[] imageBytes)
    {
        if (imageBytes.Length >= 8 &&
            imageBytes[0] == 0x89 &&
            imageBytes[1] == 0x50 &&
            imageBytes[2] == 0x4E &&
            imageBytes[3] == 0x47)
        {
            return "image/png";
        }
        if (imageBytes.Length >= 12 &&
            imageBytes[0] == (byte)'R' &&
            imageBytes[1] == (byte)'I' &&
            imageBytes[2] == (byte)'F' &&
            imageBytes[3] == (byte)'F' &&
            imageBytes[8] == (byte)'W' &&
            imageBytes[9] == (byte)'E' &&
            imageBytes[10] == (byte)'B' &&
            imageBytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        return "image/jpeg";
    }

    private static string ComposePrompt(string? instructions, string requiredContext) =>
        string.IsNullOrWhiteSpace(instructions)
            ? requiredContext
            : $"{instructions.Trim()}\n\n{requiredContext}";

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
