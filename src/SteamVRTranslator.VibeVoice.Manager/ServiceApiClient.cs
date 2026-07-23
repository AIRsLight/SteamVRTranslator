using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SteamVRTranslator.VibeVoice.Manager;

public sealed class ServiceApiClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly string _apiKey;

    public ServiceApiClient(Uri baseUri, string apiKey, HttpMessageHandler? handler = null)
    {
        _baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/");
        _apiKey = apiKey;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _client.Timeout = TimeSpan.FromSeconds(12);
    }

    public Uri BaseUri => _baseUri;

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(new Uri(_baseUri, "health"), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public Task<ServiceStatusDto> GetStatusAsync(CancellationToken cancellationToken) =>
        SendAsync<ServiceStatusDto>(HttpMethod.Get, "api/v1/status", null, cancellationToken);

    public Task<ServiceStatusDto> ConfigureAsync(
        RuntimeSettingsDto settings,
        CancellationToken cancellationToken) =>
        SendAsync<ServiceStatusDto>(
            HttpMethod.Post,
            "api/v1/configure",
            settings,
            cancellationToken);

    public Task<ServiceStatusDto> InstallAsync(
        string target,
        RuntimeSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var path = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase)
            ? "api/v1/install"
            : $"api/v1/install/{target}";
        return
        SendAsync<ServiceStatusDto>(
            HttpMethod.Post,
            path,
            settings,
            cancellationToken);
    }

    public Task<ServiceStatusDto> StartRuntimeAsync(CancellationToken cancellationToken) =>
        SendAsync<ServiceStatusDto>(HttpMethod.Post, "api/v1/runtime/start", null, cancellationToken);

    public Task<ServiceStatusDto> StopRuntimeAsync(CancellationToken cancellationToken) =>
        SendAsync<ServiceStatusDto>(HttpMethod.Post, "api/v1/runtime/stop", null, cancellationToken);

    public async Task CancelInstallAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "api/v1/install/cancel");
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(
                   new JsonSerializerOptions(JsonSerializerDefaults.Web),
                   cancellationToken)
               ?? throw new InvalidDataException("The VibeVoice service returned an empty response.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(payload)
                ? $"VibeVoice service returned HTTP {(int)response.StatusCode}."
                : payload);
    }
}

public sealed record RuntimeSettingsDto(
    string Backend,
    int DeviceIndex,
    int ThreadCount,
    string DownloadSource);

public sealed record ServiceStatusDto(
    string Status,
    bool RuntimeInstalled,
    bool ModelInstalled,
    bool RuntimeRunning,
    string Backend,
    int DeviceIndex,
    int ThreadCount,
    string DownloadSource,
    string? CurrentOperation,
    long DownloadedBytes,
    long? TotalBytes,
    string? Error,
    string DataDirectory,
    string ModelPath,
    string? RuntimePath);
