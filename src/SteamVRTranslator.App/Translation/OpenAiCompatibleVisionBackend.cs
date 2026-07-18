using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Translation;

public sealed class OpenAiCompatibleVisionBackend : ITranslationBackend
{
    private readonly TranslationConfiguration _configuration;
    private readonly TranslationProviderConfiguration _provider;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleVisionBackend(TranslationConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _provider = configuration.Providers.FirstOrDefault(provider =>
                        string.Equals(provider.Id, configuration.ActiveProviderId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("当前启用的模型 Provider 不存在。");
        if (!string.Equals(
                _provider.Type,
                TranslationProviderConfiguration.OpenAiCompatibleType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前启用的 Provider 不是 OpenAI 兼容提供商。");
        }
        _httpClient = httpClient;
    }

    public async Task<string?> TranslateAsync(
        byte[] imageBytes,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        var prompt = $"将截图中的可见文字翻译为 {_configuration.TargetLanguage}。没有可见文字时返回 [NO_TEXT]。";
        return await SendAsync(
            imageBytes,
            prompt,
            _provider.ApiKey,
            _configuration.EnableStreaming,
            onPartialResult,
            cancellationToken);
    }

    private async Task<string?> SendAsync(
        byte[] imageBytes,
        string prompt,
        string apiKey,
        bool stream,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        var endpoint = OpenAiCompatibleProviderClient.ResolveEndpoint(
            _provider.BaseUrl,
            "chat/completions");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _provider.Model,
            ["temperature"] = 0,
            ["stream"] = stream,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = _configuration.SystemPrompt
                },
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
                }
            }
        };
        if (_configuration.DisableThinking && UsesQwenThinkingProtocol(endpoint, _provider.Model))
        {
            payload["enable_thinking"] = false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        OpenAiCompatibleProviderClient.ApplyAuthorization(request, apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (stream && IsStreamingUnsupported(response.StatusCode, errorText))
            {
                return await SendAsync(
                    imageBytes,
                    prompt,
                    apiKey,
                    stream: false,
                    onPartialResult,
                    cancellationToken);
            }

            throw new InvalidOperationException(
                $"翻译接口返回 HTTP {(int)response.StatusCode}: {Limit(errorText, 500)}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (stream && string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadEventStreamAsync(response, onPartialResult, cancellationToken);
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseText);
        var result = ExtractMessageContent(document.RootElement)?.Trim();
        if (!string.IsNullOrEmpty(result))
        {
            onPartialResult?.Invoke(result);
        }

        return result;
    }

    private static async Task<string?> ReadEventStreamAsync(
        HttpResponseMessage response,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
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
            var fragment = ExtractStreamFragment(document.RootElement);
            if (string.IsNullOrEmpty(fragment))
            {
                continue;
            }

            result.Append(fragment);
            onPartialResult?.Invoke(result.ToString());
        }

        return result.ToString().Trim();
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
}
