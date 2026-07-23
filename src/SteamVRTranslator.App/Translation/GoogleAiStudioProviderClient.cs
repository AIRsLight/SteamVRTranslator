using System.Text.Json;

namespace SteamVRTranslator.App.Translation;

public sealed class GoogleAiStudioProviderClient
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    private readonly HttpClient _httpClient;

    public GoogleAiStudioProviderClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;
        for (var page = 0; page < 20; page++)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                ResolveModelsEndpoint(baseUrl, pageToken));
            ApplyApiKey(request, apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Google AI Studio 模型接口返回 HTTP {(int)response.StatusCode}: " +
                    Limit(responseText, 500));
            }

            try
            {
                using var document = JsonDocument.Parse(responseText);
                if (!document.RootElement.TryGetProperty("models", out var responseModels) ||
                    responseModels.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        "Google AI Studio 模型接口响应缺少 models 数组。");
                }

                foreach (var model in responseModels.EnumerateArray())
                {
                    if (!SupportsGenerateContent(model) ||
                        !model.TryGetProperty("name", out var nameElement))
                    {
                        continue;
                    }

                    var name = NormalizeModelName(nameElement.GetString());
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        models.Add(name);
                    }
                }

                pageToken = document.RootElement.TryGetProperty("nextPageToken", out var token)
                    ? token.GetString()
                    : null;
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    "Google AI Studio 模型接口返回的不是有效 JSON。",
                    exception);
            }

            if (string.IsNullOrWhiteSpace(pageToken))
            {
                break;
            }
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException(
                "Google AI Studio 已连接，但没有返回支持 generateContent 的模型。");
        }

        return models
            .OrderBy(ModelPreference)
            .ThenByDescending(model => model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static Uri ResolveModelsEndpoint(string baseUrl, string? pageToken = null)
    {
        var apiRoot = ResolveApiRoot(baseUrl);
        var endpoint = $"{apiRoot}/models?pageSize=1000";
        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            endpoint += "&pageToken=" + Uri.EscapeDataString(pageToken);
        }

        return new Uri(endpoint, UriKind.Absolute);
    }

    internal static Uri ResolveGenerateEndpoint(
        string baseUrl,
        string model,
        bool stream)
    {
        var normalizedModel = NormalizeModelName(model);
        if (string.IsNullOrWhiteSpace(normalizedModel))
        {
            throw new InvalidOperationException("Google AI Studio 模型不能为空。");
        }

        var operation = stream ? "streamGenerateContent?alt=sse" : "generateContent";
        return new Uri(
            $"{ResolveApiRoot(baseUrl)}/models/{Uri.EscapeDataString(normalizedModel)}:{operation}",
            UriKind.Absolute);
    }

    internal static void ApplyApiKey(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey.Trim());
        }
    }

    internal static string NormalizeModelName(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        return normalized.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? normalized["models/".Length..]
            : normalized;
    }

    private static string ResolveApiRoot(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Google AI Studio Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        }

        var normalized = uri.AbsoluteUri.TrimEnd('/');
        var knownResource = normalized.IndexOf("/models", StringComparison.OrdinalIgnoreCase);
        if (knownResource >= 0)
        {
            normalized = normalized[..knownResource];
        }

        if (!normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) &&
            !normalized.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/v1beta";
        }

        return normalized;
    }

    private static bool SupportsGenerateContent(JsonElement model)
    {
        if (!model.TryGetProperty("supportedGenerationMethods", out var methods) ||
            methods.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return methods.EnumerateArray().Any(method =>
            string.Equals(
                method.GetString(),
                "generateContent",
                StringComparison.OrdinalIgnoreCase));
    }

    private static int ModelPreference(string model)
    {
        if (string.Equals(model, "gemini-flash-latest", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (string.Equals(model, "gemini-flash-lite-latest", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }
        if (string.Equals(model, "gemini-pro-latest", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }
        if (model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) &&
            model.Contains("flash", StringComparison.OrdinalIgnoreCase) &&
            !model.Contains("image", StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }
        if (model.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }
        if (model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        return 100;
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
