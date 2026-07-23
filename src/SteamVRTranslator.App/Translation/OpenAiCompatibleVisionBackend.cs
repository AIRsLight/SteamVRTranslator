using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Translation;

public sealed class OpenAiCompatibleVisionBackend : ITranslationBackend
{
    internal static readonly TimeSpan DefaultResponseIdleTimeout = TimeSpan.FromSeconds(90);

    private readonly TranslationConfiguration _configuration;
    private readonly TranslationProviderConfiguration _provider;
    private readonly HttpClient _httpClient;
    private readonly string _customCommandSystemPrompt;
    private readonly string _customCommandPrompt;
    private readonly TimeSpan _responseIdleTimeout;
    private readonly PromptProviderPurpose _textTranslationPurpose;

    public OpenAiCompatibleVisionBackend(
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
        if (!string.Equals(
                _provider.Type,
                TranslationProviderConfiguration.OpenAiCompatibleType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前启用的 Provider 不是 OpenAI 兼容提供商。");
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

    public async Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        var prompt = ComposePrompt(
            _configuration.MarkdownTranslationPrompt,
            TargetLanguageInstruction(_configuration.TargetLanguage));
        return await SendAsync(
            imageBytes,
            prompt,
            _configuration.SystemPrompt,
            _provider.ApiKey,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(PromptProviderPurpose.DirectTranslation),
            onPartialResult,
            cancellationToken);
    }

    public async Task<string?> TranslateLayoutAsync(
        byte[] imageBytes,
        CancellationToken cancellationToken)
    {
        var prompt = ComposePrompt(
            _configuration.LayoutTranslationPrompt,
            TargetLanguageInstruction(_configuration.TargetLanguage));
        return await SendAsync(
            imageBytes,
            prompt,
            _configuration.LayoutTranslationSystemPrompt,
            _provider.ApiKey,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(PromptProviderPurpose.LayoutTranslation),
            onPartialResult: null,
            cancellationToken);
    }

    public async Task<string?> ExecuteCustomCommandAsync(
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
        var messages = BuildCustomCommandMessages(
            imageBytes,
            prompt,
            history ?? Array.Empty<AssistantConversationTurn>());
        return await SendAsync(
            imageBytes,
            prompt,
            _customCommandSystemPrompt,
            _provider.ApiKey,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(PromptProviderPurpose.CustomCommand),
            onPartialResult,
            cancellationToken,
            messages);
    }

    public async Task<string?> TranslateTextAsync(
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
            $"{TargetLanguageInstruction(targetLanguage)}\n\nSource ASR text:\n{sourceText.Trim()}");
        IReadOnlyList<object> messages =
        [
            new { role = "system", content = systemPrompt },
            new { role = "user", content = prompt }
        ];
        return await SendAsync(
            Array.Empty<byte>(),
            prompt,
            systemPrompt,
            _provider.ApiKey,
            _configuration.EnableStreaming,
            _configuration.PromptGeneration.GetFor(_textTranslationPurpose),
            onPartialResult,
            cancellationToken,
            messages);
    }

    private async Task<string?> SendAsync(
        byte[] imageBytes,
        string prompt,
        string systemPrompt,
        string apiKey,
        bool stream,
        PromptGenerationConfiguration generation,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken,
        IReadOnlyList<object>? messages = null)
    {
        var endpoint = OpenAiCompatibleProviderClient.ResolveEndpoint(
            _provider.BaseUrl,
            "chat/completions");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _provider.Model,
            ["temperature"] = generation.Temperature,
            ["top_p"] = generation.TopP,
            ["stream"] = stream,
            ["messages"] = messages ?? BuildVisionMessages(imageBytes, prompt, systemPrompt)
        };
        if (generation.MaximumOutputTokens > 0)
        {
            payload["max_tokens"] = generation.MaximumOutputTokens;
        }
        if (UsesQwenThinkingProtocol(endpoint, _provider.Model))
        {
            payload["enable_thinking"] = generation.EnableThinking;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        OpenAiCompatibleProviderClient.ApplyAuthorization(request, apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await WithIdleTimeoutAsync(
            token => _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                token),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await ReadContentAsync(response.Content, cancellationToken);
            if (stream && IsStreamingUnsupported(response.StatusCode, errorText))
            {
                return await SendAsync(
                    imageBytes,
                    prompt,
                    systemPrompt,
                    apiKey,
                    stream: false,
                    generation,
                    onPartialResult,
                    cancellationToken,
                    messages);
            }

            throw new InvalidOperationException(
                $"翻译接口返回 HTTP {(int)response.StatusCode}: {Limit(errorText, 500)}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (stream && string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadEventStreamAsync(response, onPartialResult, cancellationToken);
        }

        var responseText = await ReadContentAsync(response.Content, cancellationToken);
        using var document = JsonDocument.Parse(responseText);
        var result = ExtractMessageContent(document.RootElement)?.Trim();
        if (!string.IsNullOrEmpty(result))
        {
            onPartialResult?.Invoke(result);
        }

        return result;
    }

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
        var pending = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await WithIdleTimeoutAsync(
                token => reader.ReadAsync(buffer.AsMemory(), token).AsTask(),
                cancellationToken);
            if (count == 0)
            {
                break;
            }

            pending.Append(buffer, 0, count);
            while (TryTakeLine(pending, out var line))
            {
                if (ProcessEventStreamLine(line, result, onPartialResult))
                {
                    return result.ToString().Trim();
                }
            }
        }

        if (pending.Length > 0 &&
            ProcessEventStreamLine(pending.ToString(), result, onPartialResult))
        {
            return result.ToString().Trim();
        }

        return result.ToString().Trim();
    }

    private static bool TryTakeLine(StringBuilder pending, out string line)
    {
        for (var index = 0; index < pending.Length; index++)
        {
            if (pending[index] != '\n')
            {
                continue;
            }

            var length = index > 0 && pending[index - 1] == '\r' ? index - 1 : index;
            line = pending.ToString(0, length);
            pending.Remove(0, index + 1);
            return true;
        }

        line = string.Empty;
        return false;
    }

    private static bool ProcessEventStreamLine(
        string line,
        StringBuilder result,
        Action<string>? onPartialResult)
    {
        if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var data = line[5..].TrimStart();
        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        using var document = JsonDocument.Parse(data);
        var fragment = ExtractStreamFragment(document.RootElement);
        if (string.IsNullOrEmpty(fragment))
        {
            return false;
        }

        result.Append(fragment);
        onPartialResult?.Invoke(result.ToString());

        return false;
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

        using var idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCancellation.CancelAfter(_responseIdleTimeout);
        try
        {
            return await operation(idleCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"翻译接口连续 {_responseIdleTimeout.TotalSeconds:F0} 秒未返回新数据。",
                exception);
        }
    }

    private static string? ExtractStreamFragment(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) &&
            delta.TryGetProperty("content", out var deltaContent))
        {
            return ExtractContent(deltaContent);
        }

        return choice.TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var messageContent)
            ? ExtractContent(messageContent)
            : null;
    }

    private static string? ExtractMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var choice = choices[0];
        return choice.TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var content)
            ? ExtractContent(content)
            : null;
    }

    private static bool IsStreamingUnsupported(System.Net.HttpStatusCode statusCode, string responseText) =>
        statusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.UnprocessableEntity &&
        responseText.Contains("stream", StringComparison.OrdinalIgnoreCase);

    private static bool UsesQwenThinkingProtocol(Uri endpoint, string model) =>
        model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Host.Contains("dashscope", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Host.Contains("aliyun", StringComparison.OrdinalIgnoreCase) ||
        endpoint.Host.Contains("alibabacloud", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return string.Join(
            Environment.NewLine,
            content.EnumerateArray()
                .Where(item => item.TryGetProperty("text", out _))
                .Select(item => item.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    private static string ComposePrompt(string? instructions, string requiredContext) =>
        string.IsNullOrWhiteSpace(instructions)
            ? requiredContext
            : $"{instructions.Trim()}\n\n{requiredContext}";

    internal static string TargetLanguageInstruction(string? targetLanguage)
    {
        var normalized = targetLanguage?.Trim() ?? string.Empty;
        var (chineseName, englishName, code) = normalized.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "zh-hans" => ("中文", "Chinese", "zh-CN"),
            "ja" or "ja-jp" => ("日语", "Japanese", "ja-JP"),
            "en" or "en-us" or "en-gb" => ("英语", "English", normalized.Length == 0 ? "en-US" : normalized),
            _ => (normalized, normalized, normalized)
        };
        if (string.IsNullOrWhiteSpace(code))
        {
            return "目标语言：中文（zh-CN）\nTarget language: Chinese (zh-CN)";
        }

        return $"目标语言：{chineseName}（{code}）\nTarget language: {englishName} ({code})";
    }

    private static IReadOnlyList<object> BuildVisionMessages(
        byte[] imageBytes,
        string prompt,
        string systemPrompt) =>
        [
            new
            {
                role = "system",
                content = systemPrompt
            },
            BuildImageMessage(imageBytes, prompt)
        ];

    private IReadOnlyList<object> BuildCustomCommandMessages(
        byte[] imageBytes,
        string prompt,
        IReadOnlyList<AssistantConversationTurn> history)
    {
        List<object> messages =
        [
            new
            {
                role = "system",
                content = _customCommandSystemPrompt
            }
        ];
        if (history.Count == 0)
        {
            messages.Add(BuildImageMessage(imageBytes, prompt));
            return messages;
        }

        var firstTurn = history[0];
        messages.Add(BuildImageMessage(
            imageBytes,
            ComposePrompt(
                _customCommandPrompt,
                "用户命令：\n" + firstTurn.User)));
        messages.Add(new { role = "assistant", content = firstTurn.Assistant });
        foreach (var turn in history.Skip(1))
        {
            messages.Add(new { role = "user", content = turn.User });
            messages.Add(new { role = "assistant", content = turn.Assistant });
        }
        messages.Add(new { role = "user", content = prompt });
        return messages;
    }

    private static object BuildImageMessage(byte[] imageBytes, string prompt) =>
        new
        {
            role = "user",
            content = new object[]
            {
                new { type = "text", text = prompt },
                new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = $"data:image/jpeg;base64,{Convert.ToBase64String(imageBytes)}",
                        detail = "high"
                    }
                }
            }
        };
}
