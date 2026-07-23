using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Subtitles;

public sealed class VibeVoiceApiTranscriber : IDisposable
{
    private readonly SubtitleConfiguration _configuration;
    private readonly AppLog _log;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public VibeVoiceApiTranscriber(
        SubtitleConfiguration configuration,
        AppLog log,
        HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _log = log;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _ownsHttpClient = httpClient is null;
    }

    public async Task<VibeVoiceTranscriptionResult> TranscribeAsync(
        string audioPath,
        string language,
        string diagnosticTag,
        CancellationToken cancellationToken)
    {
        var endpoint = ResolveEndpoint(_configuration.VibeVoiceServiceUrl, "v1/audio/transcriptions");
        var stopwatch = Stopwatch.StartNew();
        var fileInfo = new FileInfo(audioPath);
        _log.Info(
            $"{diagnosticTag} [vibevoice-api] 请求开始：端点={endpoint}，" +
            $"音频={fileInfo.Length:N0} bytes，文件={fileInfo.Name}。");
        using var form = new MultipartFormDataContent();
        await using var stream = new FileStream(
            audioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var audio = new StreamContent(stream);
        audio.Headers.ContentType = new MediaTypeHeaderValue(GetAudioMediaType(fileInfo.Extension));
        form.Add(audio, "file", fileInfo.Name);
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("vibevoice-asr-q4_k"), "model");
        if (!string.IsNullOrWhiteSpace(language) &&
            !string.Equals(language, SpeechRecognitionLanguages.Automatic, StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(_configuration.VibeVoiceApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _configuration.VibeVoiceApiKey.Trim());
        }
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"VibeVoice API returned HTTP {(int)response.StatusCode}: {ResponsePreview(payload)}");
        }

        var result = ParseResponse(payload);
        stopwatch.Stop();
        _log.Info(
            $"{diagnosticTag} [vibevoice-api] 请求完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
            $"分段={result.Segments.Count}，字符={result.Text.Length}。");
        return result with { Elapsed = stopwatch.Elapsed };
    }

    public static VibeVoiceTranscriptionResult ParseResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var text = ReadString(root, "text")?.Trim() ?? string.Empty;
            var segments = new List<VibeVoiceTranscriptionSegment>();
            if (TryParseNestedSegments(text, out var nestedSegments))
            {
                segments.AddRange(nestedSegments);
                text = string.Join(" ", segments.Select(segment => segment.Text));
            }
            else if (TryGetProperty(root, "segments", out var segmentArray) &&
                segmentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentArray.EnumerateArray())
                {
                    var segmentText = (ReadString(segment, "text") ??
                                       ReadString(segment, "content") ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(segmentText) || IsNonSpeechMarker(segmentText))
                    {
                        continue;
                    }
                    segments.Add(new VibeVoiceTranscriptionSegment(
                        ReadDouble(segment, "start"),
                        ReadDouble(segment, "end"),
                        ReadSpeaker(segment),
                        segmentText));
                }
            }

            if (segments.Count == 0 && !string.IsNullOrWhiteSpace(text) && !IsNonSpeechMarker(text))
            {
                segments.Add(new VibeVoiceTranscriptionSegment(0, 0, "A", text));
            }

            return new VibeVoiceTranscriptionResult(text, segments, TimeSpan.Zero);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"VibeVoice API returned invalid JSON: {ResponsePreview(payload)}",
                exception);
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static Uri ResolveEndpoint(string baseUrl, string relativePath)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("VibeVoice service URL must be an absolute HTTP or HTTPS URL.");
        }

        return new Uri(uri.ToString().TrimEnd('/') + "/" + relativePath.TrimStart('/'));
    }

    private static bool TryParseNestedSegments(
        string text,
        out IReadOnlyList<VibeVoiceTranscriptionSegment> segments)
    {
        segments = [];
        if (!text.TrimStart().StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text.Trim().TrimEnd('.', '!'));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<VibeVoiceTranscriptionSegment>();
            foreach (var segment in document.RootElement.EnumerateArray())
            {
                var segmentText = (ReadString(segment, "content") ??
                                   ReadString(segment, "text") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(segmentText) || IsNonSpeechMarker(segmentText))
                {
                    continue;
                }
                parsed.Add(new VibeVoiceTranscriptionSegment(
                    ReadDouble(segment, "start"),
                    ReadDouble(segment, "end"),
                    ReadSpeaker(segment),
                    segmentText));
            }
            segments = parsed;
            return parsed.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement owner, string name, out JsonElement value)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement owner, string name) =>
        TryGetProperty(owner, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double ReadDouble(JsonElement owner, string name) =>
        TryGetProperty(owner, name, out var value) && value.TryGetDouble(out var number)
            ? Math.Max(0, number)
            : 0;

    private static string ReadSpeaker(JsonElement segment)
    {
        if (!TryGetProperty(segment, "speaker", out var value))
        {
            return "A";
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "A",
            JsonValueKind.Number => value.GetRawText(),
            _ => "A"
        };
    }

    private static bool IsNonSpeechMarker(string text) =>
        text.Trim() is "[Silence]" or "[Music]" or "[Noise]";

    private static string GetAudioMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".flac" => "audio/flac",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".webm" => "audio/webm",
        _ => "audio/wav"
    };

    private static string ResponsePreview(string text) =>
        text.Length <= 500 ? text : text[..500] + "...";
}

public sealed record VibeVoiceTranscriptionResult(
    string Text,
    IReadOnlyList<VibeVoiceTranscriptionSegment> Segments,
    TimeSpan Elapsed);

public sealed record VibeVoiceTranscriptionSegment(
    double StartSeconds,
    double EndSeconds,
    string Speaker,
    string Text);
