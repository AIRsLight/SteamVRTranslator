using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace SteamVRTranslator.App.AndroidMirror;

internal sealed record AndroidMirrorDownloadProgress(
    long BytesDownloaded,
    long TotalBytes,
    string Message);

internal sealed class AndroidMirrorRuntimeService : IDisposable
{
    public const string ScrcpyVersion = "4.0";
    public const string FfmpegVersion = "8.1";
    private const string FfmpegPackageName =
        "ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip";
    private const string PackageSha256 =
        "75dbeb5b00e6f64292f26f70900ae55ca397786bdfb0b9bbeb481a0549047457";
    private static readonly Uri PackageUri = new(
        "https://github.com/Genymobile/scrcpy/releases/download/v4.0/scrcpy-win64-v4.0.zip");
    private static readonly Uri FfmpegPackageUri = new(
        $"https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/{FfmpegPackageName}");
    private static readonly Uri FfmpegChecksumsUri = new(
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256");
    private static readonly string[] RequiredFiles =
    [
        "adb.exe",
        "AdbWinApi.dll",
        "AdbWinUsbApi.dll",
        "scrcpy-server",
        "avcodec-62.dll",
        "avutil-60.dll"
    ];
    private static readonly string[] FfmpegRequiredFiles =
    [
        "avcodec-62.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll"
    ];

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CancellationTokenSource? _downloadCancellation;

    public AndroidMirrorRuntimeService()
    {
        RuntimeDirectory = Path.Combine(
            Configuration.ApplicationDataPaths.RootDirectory,
            "runtimes",
            $"scrcpy-v{ScrcpyVersion}");
        FfmpegRuntimeDirectory = Path.Combine(
            Configuration.ApplicationDataPaths.RootDirectory,
            "runtimes",
            $"ffmpeg-n{FfmpegVersion}-shared");
    }

    public string RuntimeDirectory { get; }

    public string FfmpegRuntimeDirectory { get; }

    public string AdbPath => Path.Combine(RuntimeDirectory, "adb.exe");

    public string ServerPath => Path.Combine(RuntimeDirectory, "scrcpy-server");

    public bool IsInstalled => RequiredFiles.All(file =>
        File.Exists(Path.Combine(RuntimeDirectory, file)));

    public bool IsHardwareDecoderInstalled => FfmpegRequiredFiles.All(file =>
        File.Exists(Path.Combine(FfmpegRuntimeDirectory, file)));

    public string DecoderRuntimeDirectory =>
        IsHardwareDecoderInstalled ? FfmpegRuntimeDirectory : RuntimeDirectory;

    public event EventHandler<AndroidMirrorDownloadProgress>? ProgressChanged;

    public string? ResolveAvailableAdbPath()
    {
        if (File.Exists(AdbPath))
        {
            return AdbPath;
        }

        var executable = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, "adb.exe"))
            .FirstOrDefault(File.Exists);
        return executable;
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        Cancel();
        _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _downloadCancellation.Token;
        var parent = Directory.GetParent(RuntimeDirectory)?.FullName
            ?? throw new InvalidOperationException("无法确定手机镜像运行时目录。");
        Directory.CreateDirectory(parent);

        try
        {
            if (!IsInstalled)
            {
                await DownloadScrcpyAsync(parent, token);
            }
            if (!IsHardwareDecoderInstalled)
            {
                await DownloadFfmpegAsync(parent, token);
            }
            ProgressChanged?.Invoke(this, new(1, 1, "手机镜像与 FFmpeg 硬解运行时已安装。"));
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
        }
    }

    public void Cancel() => _downloadCancellation?.Cancel();

    public void Dispose()
    {
        Cancel();
        _downloadCancellation?.Dispose();
        _httpClient.Dispose();
    }

    private async Task DownloadScrcpyAsync(string parent, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(parent, $".scrcpy-{Guid.NewGuid():N}");
        var archivePath = tempRoot + ".zip";
        try
        {
            var total = await DownloadFileAsync(
                PackageUri,
                archivePath,
                "正在下载 scrcpy 运行时...",
                cancellationToken);
            await VerifyHashAsync(
                archivePath,
                PackageSha256,
                "scrcpy",
                cancellationToken);
            Directory.CreateDirectory(tempRoot);
            ZipFile.ExtractToDirectory(archivePath, tempRoot);
            var extractedRoot = Directory
                .EnumerateDirectories(tempRoot, "scrcpy-win64-*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault() ?? tempRoot;
            EnsureRequiredFiles(extractedRoot, RequiredFiles, "scrcpy");
            Directory.CreateDirectory(RuntimeDirectory);
            foreach (var file in Directory.EnumerateFiles(extractedRoot))
            {
                File.Copy(file, Path.Combine(RuntimeDirectory, Path.GetFileName(file)), overwrite: true);
            }
            ProgressChanged?.Invoke(this, new(total, total, "scrcpy 运行时已安装。"));
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task DownloadFfmpegAsync(string parent, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(parent, $".ffmpeg-{Guid.NewGuid():N}");
        var archivePath = tempRoot + ".zip";
        try
        {
            ProgressChanged?.Invoke(this, new(0, 0, "正在获取 FFmpeg 校验信息..."));
            var checksums = await _httpClient.GetStringAsync(FfmpegChecksumsUri, cancellationToken);
            var expectedHash = ParsePackageHash(checksums, FfmpegPackageName);
            var total = await DownloadFileAsync(
                FfmpegPackageUri,
                archivePath,
                "正在下载 FFmpeg D3D11VA 运行时...",
                cancellationToken);
            await VerifyHashAsync(
                archivePath,
                expectedHash,
                "FFmpeg",
                cancellationToken);
            Directory.CreateDirectory(tempRoot);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var fileName in FfmpegRequiredFiles.Append("LICENSE.txt"))
                {
                    var entry = archive.Entries.FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, fileName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"FFmpeg 发布包缺少 {fileName}。");
                    entry.ExtractToFile(Path.Combine(tempRoot, fileName), overwrite: true);
                }
            }
            EnsureRequiredFiles(tempRoot, FfmpegRequiredFiles, "FFmpeg");
            Directory.CreateDirectory(FfmpegRuntimeDirectory);
            foreach (var file in Directory.EnumerateFiles(tempRoot))
            {
                File.Copy(
                    file,
                    Path.Combine(FfmpegRuntimeDirectory, Path.GetFileName(file)),
                    overwrite: true);
            }
            ProgressChanged?.Invoke(this, new(total, total, "FFmpeg D3D11VA 运行时已安装。"));
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<long> DownloadFileAsync(
        Uri uri,
        string destination,
        string message,
        CancellationToken cancellationToken)
    {
        ProgressChanged?.Invoke(this, new(0, 0, message));
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            useAsync: true);
        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        var progressInterval = Stopwatch.StartNew();
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (progressInterval.ElapsedMilliseconds >= 100)
            {
                ProgressChanged?.Invoke(this, new(downloaded, total, message));
                progressInterval.Restart();
            }
        }
        ProgressChanged?.Invoke(this, new(downloaded, total, message));
        return downloaded;
    }

    internal static string ParsePackageHash(string checksums, string packageName)
    {
        var line = checksums
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(candidate => candidate.EndsWith(
                packageName,
                StringComparison.OrdinalIgnoreCase));
        var hash = line?.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"FFmpeg 校验清单缺少 {packageName}。");
        }
        return hash.ToLowerInvariant();
    }

    private static void EnsureRequiredFiles(
        string directory,
        IEnumerable<string> requiredFiles,
        string packageName)
    {
        foreach (var requiredFile in requiredFiles)
        {
            if (!File.Exists(Path.Combine(directory, requiredFile)))
            {
                throw new InvalidDataException($"{packageName} 发布包缺少 {requiredFile}。");
            }
        }
    }

    private static async Task VerifyHashAsync(
        string path,
        string expectedHash,
        string packageName,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{packageName} 下载校验失败。期望 {expectedHash}，实际 {actual}。");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
