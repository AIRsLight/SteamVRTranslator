using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SteamVRTranslator.App.Speech;

public sealed class SenseVoiceDownloadService : IDisposable
{
    private const int MaximumAttempts = 3;
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeDownload;

    public SenseVoiceDownloadService(string rootDirectory, HttpClient? httpClient = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _httpClient = httpClient ?? CreateHttpClient();
        OwnsHttpClient = httpClient is null;
    }

    public event EventHandler<SenseVoiceDownloadProgress>? ProgressChanged;

    private bool OwnsHttpClient { get; }

    public IReadOnlyList<SenseVoiceAssetStatus> GetStatuses(string modelVariant)
    {
        return SenseVoiceAssetCatalog.ResolveBundle(modelVariant)
            .Select(asset => new SenseVoiceAssetStatus(
                asset.Id,
                asset.DisplayName,
                asset.TargetPath,
                asset.Size,
                IsInstalled(asset)))
            .ToArray();
    }

    public async Task DownloadBundleAsync(
        string modelVariant,
        bool useMirror,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource operationCancellation;
        lock (_sync)
        {
            if (_activeDownload is not null)
            {
                throw new InvalidOperationException("已有 ASR 下载任务正在运行。");
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeDownload = operationCancellation;
        }

        var assets = SenseVoiceAssetCatalog.ResolveBundle(modelVariant);
        var total = assets.Sum(asset => asset.Size);
        long completed = 0;
        try
        {
            foreach (var asset in assets)
            {
                operationCancellation.Token.ThrowIfCancellationRequested();
                if (await IsValidAsync(asset, operationCancellation.Token))
                {
                    completed += asset.Size;
                    Report(asset, completed, total, "已校验");
                    continue;
                }

                await DownloadWithRetryAsync(
                    asset,
                    useMirror,
                    completed,
                    total,
                    operationCancellation.Token);
                completed += asset.Size;
            }

            ProgressChanged?.Invoke(
                this,
                new SenseVoiceDownloadProgress("complete", string.Empty, total, total, "SenseVoice 已安装并校验。"));
        }
        finally
        {
            lock (_sync)
            {
                _activeDownload?.Dispose();
                _activeDownload = null;
            }
        }
    }

    public bool Cancel()
    {
        lock (_sync)
        {
            if (_activeDownload is null)
            {
                return false;
            }

            _activeDownload.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        Cancel();
        if (OwnsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task DownloadWithRetryAsync(
        SenseVoiceAsset asset,
        bool useMirror,
        long completed,
        long total,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            var targetPath = ResolveTarget(asset);
            var temporaryPath = targetPath + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Delete(temporaryPath);
                await DownloadOnceAsync(asset, useMirror, temporaryPath, completed, total, cancellationToken);
                await VerifyFileAsync(temporaryPath, asset, cancellationToken);
                File.Move(temporaryPath, targetPath, true);
                Report(asset, completed + asset.Size, total, "已安装");
                return;
            }
            catch (OperationCanceledException)
            {
                File.Delete(temporaryPath);
                throw;
            }
            catch (Exception exception)
            {
                File.Delete(temporaryPath);
                lastError = exception;
                if (attempt < MaximumAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                }
            }
        }

        throw new InvalidOperationException(
            $"下载 {asset.DisplayName} 失败，已重试 {MaximumAttempts} 次。",
            lastError);
    }

    private async Task DownloadOnceAsync(
        SenseVoiceAsset asset,
        bool useMirror,
        string temporaryPath,
        long completed,
        long total,
        CancellationToken cancellationToken)
    {
        var url = asset.GetUrl(useMirror);
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载服务器返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})。URL={url}");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 128);
        long downloaded = 0;
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                downloaded += count;
                ProgressChanged?.Invoke(
                    this,
                    new SenseVoiceDownloadProgress(
                        "downloading",
                        asset.DisplayName,
                        completed + downloaded,
                        total,
                        $"正在下载 {asset.DisplayName}"));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<bool> IsValidAsync(SenseVoiceAsset asset, CancellationToken cancellationToken)
    {
        var path = ResolveTarget(asset);
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size)
        {
            return false;
        }

        try
        {
            await VerifyFileAsync(path, asset, cancellationToken);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task VerifyFileAsync(
        string path,
        SenseVoiceAsset asset,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != asset.Size)
        {
            throw new InvalidDataException(
                $"{asset.DisplayName} 文件长度不正确：预期 {asset.Size:N0}，实际 {info.Length:N0}。 ");
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{asset.DisplayName} SHA-256 校验失败。");
        }
    }

    private bool IsInstalled(SenseVoiceAsset asset)
    {
        var path = ResolveTarget(asset);
        return File.Exists(path) && new FileInfo(path).Length == asset.Size;
    }

    private string ResolveTarget(SenseVoiceAsset asset) =>
        Path.GetFullPath(Path.Combine(_rootDirectory, asset.TargetPath));

    private void Report(SenseVoiceAsset asset, long completed, long total, string state) =>
        ProgressChanged?.Invoke(
            this,
            new SenseVoiceDownloadProgress(state, asset.DisplayName, completed, total, $"{asset.DisplayName}：{state}"));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamVRTranslator", "1.0"));
        return client;
    }
}

public sealed record SenseVoiceDownloadProgress(
    string State,
    string AssetName,
    long BytesDownloaded,
    long TotalBytes,
    string Message);

public sealed record SenseVoiceAssetStatus(
    string Id,
    string DisplayName,
    string TargetPath,
    long Size,
    bool Installed);

internal sealed record SenseVoiceAsset(
    string Id,
    string DisplayName,
    string Repository,
    string Revision,
    string FileName,
    string TargetPath,
    long Size,
    string Sha256)
{
    public string GetUrl(bool useMirror)
    {
        var host = useMirror ? "hf-mirror.com" : "huggingface.co";
        var path = string.Join('/', FileName.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
        return $"https://{host}/{Repository}/resolve/{Revision}/{path}?download=true";
    }
}

internal static class SenseVoiceAssetCatalog
{
    private static readonly SenseVoiceAsset CpuRuntime = new(
        "runtime-cpu",
        "SenseVoice CPU 运行时",
        "AIRsLight/VRChatVoiceInput-Runtimes",
        "2a188dfe4289b3a37d374dbd2352f0fa9ac5f5d0",
        "runtimes/sensevoice-cpu/llama-funasr-sensevoice.exe",
        "runtimes/llama-funasr-sensevoice.exe",
        1916416,
        "977fcd532c7b1465a6e5b73916e0db878001adea0cf5bf94621e3c36ec59a745");

    private static readonly SenseVoiceAsset Q8 = new(
        "q8_0",
        "SenseVoice Q8_0",
        "FunAudioLLM/SenseVoiceSmall-GGUF",
        "90c1c61912018b70ada0fcc024ea24aca62f2e63",
        "sensevoice-small-q8.gguf",
        "models/sensevoice-small-q8.gguf",
        254208320,
        "4ae45c94422de949b387e2e0fb10d7e14e4c42c69db30c3444ecc7d4b844b7c5");

    private static readonly SenseVoiceAsset Q5 = new(
        "q5_0",
        "SenseVoice Q5_0",
        "AIRsLight/SenseVoiceSmall-GGUF",
        "d6bfce12fb0369874d357e9ebeed012e6349fd25",
        "sensevoice-small-q5_0.gguf",
        "models/sensevoice-small-q5_0.gguf",
        167117312,
        "24114cc2663de19da1f8c53c2232d9c98f8a6d9e663b2ab4766b414e691b6818");

    private static readonly SenseVoiceAsset Vad = new(
        "fsmn-vad",
        "FSMN VAD",
        "FunAudioLLM/fsmn-vad-GGUF",
        "6840bae4c5c92ee8c04faaf4db23dd0105098d7f",
        "fsmn-vad.gguf",
        "models/fsmn-vad.gguf",
        1720512,
        "1270f2559c495f4e7b6e739541151027d360761a3fda43fc147034f5719f5479");

    public static IReadOnlyList<SenseVoiceAsset> ResolveBundle(string modelVariant) =>
        string.Equals(modelVariant, "q5_0", StringComparison.OrdinalIgnoreCase)
            ? [CpuRuntime, Q5, Vad]
            : [CpuRuntime, Q8, Vad];
}
