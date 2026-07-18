using System.Net.Http.Headers;
using System.Text.Json;

namespace SteamVRTranslator.App.Translation;

public sealed class OpenAiCompatibleProviderClient
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleProviderClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ResolveEndpoint(baseUrl, "models"));
        ApplyAuthorization(request, apiKey);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"模型接口返回 HTTP {(int)response.StatusCode}: {Limit(responseText, 500)}");
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("模型接口响应缺少 OpenAI 兼容的 data 数组。");
            }

            var models = data.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out _))
                .Select(item => item.GetProperty("id").GetString()?.Trim())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (models.Length == 0)
            {
                throw new InvalidOperationException("模型接口已连接，但没有返回可选择的模型。");
            }

            return models;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("模型接口返回的不是有效 JSON。", exception);
        }
    }

    internal static Uri ResolveEndpoint(string baseUrl, string resource)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Base URL 必须是有效的 HTTP 或 HTTPS 地址。");
        }

        var normalized = uri.AbsoluteUri.TrimEnd('/');
        foreach (var knownSuffix in new[] { "/chat/completions", "/models" })
        {
            if (normalized.EndsWith(knownSuffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^knownSuffix.Length];
                break;
            }
        }
        var suffix = "/" + resource.Trim('/');
        if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            normalized += suffix;
        }

        return new Uri(normalized, UriKind.Absolute);
    }

    internal static void ApplyAuthorization(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
