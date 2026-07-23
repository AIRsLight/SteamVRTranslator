using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SteamVRTranslator.VibeVoice.Server;

public sealed class VibeVoiceRuntimeManager : IAsyncDisposable
{
    private const string ModelFileName = "vibevoice-asr-q4_k.gguf";
    private const string CrispAsrVersion = "v0.8.15";
    private const string ModelSha256 =
        "f1e87bb5c25dd469b495759e59c4554c4e8ec254f36c5c659737ff3e61ace982";
    private const string OfficialModelUrl =
        "https://huggingface.co/cstr/vibevoice-asr-GGUF/resolve/main/vibevoice-asr-q4_k.gguf";
    private readonly ServiceOptions _options;
    private readonly ILogger<VibeVoiceRuntimeManager> _log;
    private readonly HttpClient _downloadClient;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly string _dataDirectory;
    private readonly string _settingsPath;
    private readonly string _modelPath;
    private Process? _runtimeProcess;
    private HttpClient? _runtimeClient;
    private Uri? _runtimeBaseUri;
    private Task? _installTask;
    private VibeVoiceRuntimeSettings _settings;
    private string? _operation;
    private long _downloadedBytes;
    private long? _totalBytes;
    private string? _lastError;
    private bool _disposed;

    public VibeVoiceRuntimeManager(
        ServiceOptions options,
        ILogger<VibeVoiceRuntimeManager> log)
    {
        _options = options;
        _log = log;
        _dataDirectory = options.ResolveDataDirectory();
        _settingsPath = Path.Combine(_dataDirectory, "runtime-settings.json");
        _modelPath = Path.Combine(_dataDirectory, "models", ModelFileName);
        Directory.CreateDirectory(_dataDirectory);
        _settings = LoadSettings(options);
        _downloadClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("SteamVRTranslator-VibeVoiceServer/1.0");
    }

    public ServiceStatus GetStatus()
    {
        lock (_statusLock)
        {
            var runtimePath = ResolveRuntimeExecutable(_settings.Backend);
            var running = _runtimeProcess is { HasExited: false };
            var status = _operation is not null
                ? "installing"
                : running
                    ? "ready"
                    : IsRuntimeInstalled(runtimePath) && IsModelInstalled()
                        ? "stopped"
                        : "not-installed";
            return new ServiceStatus(
                status,
                IsRuntimeInstalled(runtimePath),
                IsModelInstalled(),
                running,
                _settings.Backend,
                _settings.DeviceIndex,
                _settings.ThreadCount,
                _settings.DownloadSource,
                _operation,
                _downloadedBytes,
                _totalBytes,
                _lastError,
                _dataDirectory,
                _modelPath,
                runtimePath);
        }
    }

    public bool IsAuthorized(HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return true;
        }

        var authorization = request.Headers.Authorization.ToString();
        if (AuthenticationHeaderValue.TryParse(authorization, out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            FixedTimeEquals(header.Parameter, _options.ApiKey))
        {
            return true;
        }

        return FixedTimeEquals(request.Headers["X-API-Key"].ToString(), _options.ApiKey);
    }

    public bool StartInstall(InstallRequest? request)
    {
        lock (_statusLock)
        {
            if (_installTask is { IsCompleted: false })
            {
                return false;
            }

            ApplySettings(request?.Backend, request?.DeviceIndex, request?.ThreadCount, request?.DownloadSource);
            _lastError = null;
            _installTask = Task.Run(() => InstallAsync(CancellationToken.None));
            return true;
        }
    }

    public async Task ConfigureAsync(ConfigureRequest request, CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            var previousBackend = _settings.Backend;
            var previousDevice = _settings.DeviceIndex;
            var previousThreads = _settings.ThreadCount;
            ApplySettings(request.Backend, request.DeviceIndex, request.ThreadCount, request.DownloadSource);
            SaveSettings();
            if (_runtimeProcess is { HasExited: false } &&
                (!string.Equals(previousBackend, _settings.Backend, StringComparison.OrdinalIgnoreCase) ||
                 previousDevice != _settings.DeviceIndex || previousThreads != _settings.ThreadCount))
            {
                StopRuntimeCore();
                await StartRuntimeCoreAsync(cancellationToken);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StartRuntimeAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StartRuntimeCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopRuntimeAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            StopRuntimeCore();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<HttpResponseMessage> ProxyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        await StartRuntimeAsync(cancellationToken);
        var client = _runtimeClient ?? throw new InvalidOperationException("VibeVoice runtime is unavailable.");
        var baseUri = _runtimeBaseUri ?? throw new InvalidOperationException("VibeVoice runtime endpoint is unavailable.");
        var target = new Uri(baseUri, request.Path + request.QueryString);
        using var proxyRequest = new HttpRequestMessage(new HttpMethod(request.Method), target)
        {
            Content = new StreamContent(request.Body)
        };
        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            proxyRequest.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        }
        if (request.ContentLength is not null)
        {
            proxyRequest.Content.Headers.ContentLength = request.ContentLength;
        }

        return await client.SendAsync(
            proxyRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleLock.WaitAsync();
        try
        {
            StopRuntimeCore();
            _downloadClient.Dispose();
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
        }
    }

    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _lifecycleLock.WaitAsync(cancellationToken);
            try
            {
                StopRuntimeCore();
                SaveSettings();
                await InstallRuntimeAsync(cancellationToken);
                await InstallModelAsync(cancellationToken);
                if (_options.AutoStartRuntime)
                {
                    await StartRuntimeCoreAsync(cancellationToken);
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        catch (Exception exception)
        {
            lock (_statusLock)
            {
                _lastError = exception.Message;
            }
            _log.LogError(exception, "VibeVoice installation failed.");
        }
        finally
        {
            lock (_statusLock)
            {
                _operation = null;
                _downloadedBytes = 0;
                _totalBytes = null;
            }
        }
    }

    private async Task InstallRuntimeAsync(CancellationToken cancellationToken)
    {
        var runtimePath = ResolveRuntimeExecutable(_settings.Backend);
        if (IsRuntimeInstalled(runtimePath))
        {
            _log.LogInformation("CrispASR {Backend} runtime is already installed.", _settings.Backend);
            return;
        }

        var archiveName = RuntimeArchiveName(_settings.Backend);
        var archivePath = Path.Combine(_dataDirectory, "downloads", archiveName);
        var url = $"https://github.com/CrispStrobe/CrispASR/releases/download/{CrispAsrVersion}/{archiveName}";
        await DownloadFileAsync(
            url,
            archivePath,
            $"runtime:{_settings.Backend}",
            RuntimeArchiveSha256(archiveName),
            cancellationToken);

        var runtimeDirectory = RuntimeDirectory(_settings.Backend);
        var temporaryDirectory = runtimeDirectory + ".installing";
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, true);
        }
        Directory.CreateDirectory(temporaryDirectory);
        if (archiveName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, temporaryDirectory, true);
        }
        else
        {
            await ExtractTarGzAsync(archivePath, temporaryDirectory, cancellationToken);
        }

        var extractedExecutable = FindRuntimeExecutable(temporaryDirectory)
            ?? throw new InvalidDataException("The CrispASR archive did not contain its server executable.");
        if (Directory.Exists(runtimeDirectory))
        {
            Directory.Delete(runtimeDirectory, true);
        }
        Directory.Move(temporaryDirectory, runtimeDirectory);
        runtimePath = FindRuntimeExecutable(runtimeDirectory)
            ?? throw new InvalidDataException("The installed CrispASR executable cannot be located.");
        _log.LogInformation("Installed CrispASR runtime at {Path}.", runtimePath);
    }

    private async Task InstallModelAsync(CancellationToken cancellationToken)
    {
        if (IsModelInstalled())
        {
            _log.LogInformation("VibeVoice Q4_K model is already installed.");
            return;
        }

        var url = string.Equals(_settings.DownloadSource, "hf-mirror", StringComparison.OrdinalIgnoreCase)
            ? OfficialModelUrl.Replace("https://huggingface.co", "https://hf-mirror.com", StringComparison.Ordinal)
            : OfficialModelUrl;
        await DownloadFileAsync(url, _modelPath, "model:q4_k", ModelSha256, cancellationToken);
        if (!IsModelInstalled())
        {
            File.Delete(_modelPath);
            throw new InvalidDataException("The downloaded VibeVoice model is not a valid GGUF file.");
        }
        _log.LogInformation("Installed VibeVoice Q4_K model at {Path}.", _modelPath);
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        string operation,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryPath = destination + ".part";
        var existingLength = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        lock (_statusLock)
        {
            _operation = operation;
            _downloadedBytes = existingLength;
            _totalBytes = null;
        }
        using var response = await _downloadClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (existingLength > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            existingLength = 0;
        }
        response.EnsureSuccessStatusCode();
        var remainingLength = response.Content.Headers.ContentLength;
        lock (_statusLock)
        {
            _downloadedBytes = existingLength;
            _totalBytes = remainingLength is null ? null : existingLength + remainingLength.Value;
        }

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(
                         temporaryPath,
                         existingLength == 0 ? FileMode.Create : FileMode.Append,
                         FileAccess.Write,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                lock (_statusLock)
                {
                    _downloadedBytes += read;
                }
            }
            await output.FlushAsync(cancellationToken);
        }

        lock (_statusLock)
        {
            _operation = $"verify:{operation}";
        }
        string actualHash;
        await using (var verificationStream = new FileStream(
                         temporaryPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(verificationStream, cancellationToken));
        }
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temporaryPath);
            throw new InvalidDataException(
                $"SHA-256 verification failed for {Path.GetFileName(destination)}. " +
                $"Expected {expectedSha256}, received {actualHash}.");
        }
        File.Move(temporaryPath, destination, true);
    }

    private async Task StartRuntimeCoreAsync(CancellationToken cancellationToken)
    {
        if (_runtimeProcess is { HasExited: false })
        {
            return;
        }

        var runtimePath = ResolveRuntimeExecutable(_settings.Backend);
        if (!IsRuntimeInstalled(runtimePath) || !IsModelInstalled())
        {
            throw new InvalidOperationException(
                "VibeVoice runtime or Q4_K model is not installed. Open the service manager and install it first.");
        }

        var port = ReserveLoopbackPort();
        var startInfo = new ProcessStartInfo
        {
            FileName = runtimePath,
            WorkingDirectory = Path.GetDirectoryName(runtimePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        AddArgument(startInfo, "--server");
        AddArgument(startInfo, "--host", "127.0.0.1");
        AddArgument(startInfo, "--port", port.ToString());
        AddArgument(startInfo, "--backend", "vibevoice");
        AddArgument(startInfo, "--model", _modelPath);
        AddArgument(startInfo, "--threads", _settings.ThreadCount.ToString());
        switch (_settings.Backend)
        {
            case "vulkan":
                AddArgument(startInfo, "--gpu-backend", "vulkan");
                AddArgument(startInfo, "--device", _settings.DeviceIndex.ToString());
                break;
            case "cuda":
                AddArgument(startInfo, "--gpu-backend", "cuda");
                AddArgument(startInfo, "--device", _settings.DeviceIndex.ToString());
                break;
            default:
                AddArgument(startInfo, "--no-gpu");
                break;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) => LogRuntimeLine(eventArgs.Data, false);
        process.ErrorDataReceived += (_, eventArgs) => LogRuntimeLine(eventArgs.Data, true);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Unable to start the CrispASR VibeVoice runtime.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _runtimeProcess = process;
        _runtimeBaseUri = new Uri($"http://127.0.0.1:{port}/");
        _runtimeClient?.Dispose();
        _runtimeClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        _log.LogInformation(
            "Starting resident VibeVoice runtime. PID={Pid}, backend={Backend}, device={Device}, threads={Threads}.",
            process.Id,
            _settings.Backend,
            _settings.DeviceIndex,
            _settings.ThreadCount);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            Exception? lastException = null;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"CrispASR exited while loading VibeVoice. Exit code={process.ExitCode}.");
                }
                try
                {
                    using var response = await _runtimeClient.GetAsync(
                        new Uri(_runtimeBaseUri, "health"),
                        cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        lock (_statusLock)
                        {
                            _lastError = null;
                        }
                        _log.LogInformation("VibeVoice runtime is ready.");
                        return;
                    }
                }
                catch (HttpRequestException exception)
                {
                    lastException = exception;
                }
                await Task.Delay(400, cancellationToken);
            }

            throw new TimeoutException("Timed out waiting for VibeVoice to load.", lastException);
        }
        catch
        {
            StopRuntimeCore();
            throw;
        }
    }

    private void StopRuntimeCore()
    {
        var process = _runtimeProcess;
        _runtimeProcess = null;
        _runtimeBaseUri = null;
        _runtimeClient?.Dispose();
        _runtimeClient = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ApplySettings(
        string? backend,
        int? deviceIndex,
        int? threadCount,
        string? downloadSource)
    {
        _settings.Backend = VibeVoiceRuntimeSettings.NormalizeBackend(backend ?? _settings.Backend);
        _settings.DeviceIndex = deviceIndex ?? _settings.DeviceIndex;
        _settings.ThreadCount = threadCount ?? _settings.ThreadCount;
        _settings.DownloadSource = downloadSource ?? _settings.DownloadSource;
        _settings.Normalize();
    }

    private VibeVoiceRuntimeSettings LoadSettings(ServiceOptions options)
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<VibeVoiceRuntimeSettings>(File.ReadAllText(_settingsPath));
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            _log.LogWarning(exception, "Unable to read persisted VibeVoice runtime settings.");
        }

        var settings = new VibeVoiceRuntimeSettings
        {
            Backend = options.Backend,
            DeviceIndex = options.DeviceIndex,
            ThreadCount = options.ThreadCount,
            DownloadSource = options.DownloadSource
        };
        settings.Normalize();
        return settings;
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(_dataDirectory);
        var temporary = _settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_settings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        File.Move(temporary, _settingsPath, true);
    }

    private string RuntimeDirectory(string backend) =>
        Path.Combine(_dataDirectory, "runtimes", backend);

    private string? ResolveRuntimeExecutable(string backend) =>
        FindRuntimeExecutable(RuntimeDirectory(backend));

    private static string? FindRuntimeExecutable(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "crispasr.exe"
            : "crispasr";
        return Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static bool IsRuntimeInstalled(string? runtimePath) =>
        !string.IsNullOrWhiteSpace(runtimePath) && File.Exists(runtimePath);

    private bool IsModelInstalled()
    {
        if (!File.Exists(_modelPath) || new FileInfo(_modelPath).Length < 1024L * 1024 * 1024)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(_modelPath);
        return stream.Read(header) == header.Length &&
               header.SequenceEqual("GGUF"u8);
    }

    private static string RuntimeArchiveName(string backend)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return backend switch
            {
                "cuda" => "crispasr-windows-x86_64-cuda.zip",
                "vulkan" => "crispasr-windows-x86_64-vulkan.zip",
                _ => "crispasr-windows-x86_64-cpu.zip"
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return backend switch
            {
                "cuda" => "crispasr-linux-x86_64-cuda.tar.gz",
                "vulkan" => "crispasr-linux-x86_64-vulkan.tar.gz",
                _ => "crispasr-linux-x86_64.tar.gz"
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && backend == "cpu")
        {
            return "crispasr-macos.tar.gz";
        }
        throw new PlatformNotSupportedException(
            $"No packaged CrispASR runtime is available for {RuntimeInformation.OSDescription} / {backend}.");
    }

    private static string RuntimeArchiveSha256(string archiveName) => archiveName switch
    {
        "crispasr-windows-x86_64-cpu.zip" =>
            "652bd964381ef716e0fd06bb975a7a4775855111d47bd29e0abb56c8e3515885",
        "crispasr-windows-x86_64-vulkan.zip" =>
            "a13e9714e8b8a6318c32b56442b914338d39aeafd088b7fd49262daccfd74d5b",
        "crispasr-windows-x86_64-cuda.zip" =>
            "478a74780a514ede1d5cc2d66074252e94855c90628f3feafee0caa378ed2e4b",
        "crispasr-linux-x86_64.tar.gz" =>
            "ecae0527284423c3908a0dbb6bac8046a2206298dc7849e6eb0390cd4a711a30",
        "crispasr-linux-x86_64-vulkan.tar.gz" =>
            "44d73ee2ec51df01ddd97fc4ba1cd98bd770290e1f410c02a2eb7b08f081ca78",
        "crispasr-linux-x86_64-cuda.tar.gz" =>
            "e89f460523ac8ef9018a1e7d120b974d51c252b98b7c8be02f5e6e593f296dc4",
        "crispasr-macos.tar.gz" =>
            "80c0a4fdbc1c44de08c4a92cf04d9301192e65b96d84f4e26d1a9f57e1703f14",
        _ => throw new PlatformNotSupportedException(
            $"No verified CrispASR archive is registered for {archiveName}.")
    };

    private static async Task ExtractTarGzAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(
            gzip,
            destination,
            overwriteFiles: true,
            cancellationToken);
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void AddArgument(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (var value in values)
        {
            startInfo.ArgumentList.Add(value);
        }
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private void LogRuntimeLine(string? line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (error)
        {
            _log.LogInformation("[crispasr] {Line}", line);
        }
        else
        {
            _log.LogDebug("[crispasr] {Line}", line);
        }
    }
}
