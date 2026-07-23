using System.Buffers;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace SteamVRTranslator.App.Subtitles;

public sealed class SubtitleDiarizationDownloadService : IDisposable
{
    private const int MaximumAttempts = 3;
    private readonly HttpClient _httpClient;
    private readonly string _rootDirectory;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeDownload;

    public SubtitleDiarizationDownloadService(string rootDirectory, HttpClient? httpClient = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _httpClient = httpClient ?? CreateHttpClient();
        OwnsHttpClient = httpClient is null;
    }

    public event EventHandler<SubtitleModelDownloadProgress>? ProgressChanged;

    private bool OwnsHttpClient { get; }

    public IReadOnlyList<SubtitleModelAssetStatus> GetStatuses() =>
        SubtitleDiarizationAssetCatalog.Assets
            .Select(asset => new SubtitleModelAssetStatus(
                asset.Id,
                asset.DisplayName,
                asset.TargetPath,
                asset.Size,
                IsInstalled(asset)))
            .ToArray();

    public async Task DownloadAsync(bool useMirror, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource operationCancellation;
        lock (_sync)
        {
            if (_activeDownload is not null)
            {
                throw new InvalidOperationException("说话人模型下载已在进行中。");
            }

            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeDownload = operationCancellation;
        }

        var total = SubtitleDiarizationAssetCatalog.Assets.Sum(asset => asset.Size);
        long completed = 0;
        try
        {
            foreach (var asset in SubtitleDiarizationAssetCatalog.Assets)
            {
                if (await IsValidAsync(asset, operationCancellation.Token))
                {
                    completed += asset.Size;
                    Report(asset, completed, total, "verified");
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
                new SubtitleModelDownloadProgress("complete", string.Empty, total, total));
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
        SubtitleDiarizationAsset asset,
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
                Report(asset, completed + asset.Size, total, "installed");
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
        SubtitleDiarizationAsset asset,
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
        response.EnsureSuccessStatusCode();
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
                    new SubtitleModelDownloadProgress(
                        "downloading",
                        asset.DisplayName,
                        completed + downloaded,
                        total));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<bool> IsValidAsync(
        SubtitleDiarizationAsset asset,
        CancellationToken cancellationToken)
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
        SubtitleDiarizationAsset asset,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != asset.Size)
        {
            throw new InvalidDataException($"{asset.DisplayName} 文件大小不匹配。");
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{asset.DisplayName} SHA256 校验失败。");
        }
    }

    private bool IsInstalled(SubtitleDiarizationAsset asset)
    {
        var path = ResolveTarget(asset);
        return File.Exists(path) && new FileInfo(path).Length == asset.Size;
    }

    private string ResolveTarget(SubtitleDiarizationAsset asset) =>
        Path.GetFullPath(Path.Combine(_rootDirectory, asset.TargetPath));

    private void Report(
        SubtitleDiarizationAsset asset,
        long completed,
        long total,
        string state) =>
        ProgressChanged?.Invoke(
            this,
            new SubtitleModelDownloadProgress(state, asset.DisplayName, completed, total));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SteamVRTranslator", "1.0"));
        return client;
    }
}

public sealed record SubtitleModelDownloadProgress(
    string State,
    string AssetName,
    long BytesDownloaded,
    long TotalBytes);

public sealed record SubtitleModelAssetStatus(
    string Id,
    string DisplayName,
    string TargetPath,
    long Size,
    bool Installed);

internal sealed record SubtitleDiarizationAsset(
    string Id,
    string DisplayName,
    string TargetPath,
    long Size,
    string Sha256,
    string OfficialUrl,
    string MirrorUrl)
{
    public string GetUrl(bool useMirror) => useMirror ? MirrorUrl : OfficialUrl;
}

internal static class SubtitleDiarizationAssetCatalog
{
    public static readonly IReadOnlyList<SubtitleDiarizationAsset> Assets =
    [
        new(
            "segmentation-int8",
            "Pyannote 3.0 INT8 分段模型",
            "models/subtitle-diarization/pyannote-segmentation-3.0-int8.onnx",
            1540514,
            "10A438C2E0D90ED5F5DA545CEC2244D887315F6DBBBF1D3D564D00745B01952E",
            "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/340b52f1f5cd12d45a30fa284691417eaad2ff92/model.int8.onnx?download=true",
            "https://hf-mirror.com/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/340b52f1f5cd12d45a30fa284691417eaad2ff92/model.int8.onnx?download=true"),
        new(
            "embedding-eres2net",
            "3D-Speaker ERes2Net 声纹模型",
            "models/subtitle-diarization/3dspeaker-eres2net.onnx",
            39593761,
            "1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            "https://hf-mirror.com/DeepBeepMeep/LTX-2/resolve/07ff86eeb0404c1d7b033e930234bcb4f49f021a/sherpa/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx?download=true")
    ];
}
