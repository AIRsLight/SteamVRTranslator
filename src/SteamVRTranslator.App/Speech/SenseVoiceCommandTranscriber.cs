using System.Diagnostics;
using System.Text;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Speech;

public sealed class SenseVoiceCommandTranscriber : IDisposable
{
    private readonly AppLog _log;
    private readonly SenseVoiceResidentWorker _worker;

    public SenseVoiceCommandTranscriber(SpeechConfiguration configuration, AppLog log)
    {
        _log = log;
        var useVulkan = string.Equals(
            configuration.SenseVoiceBackend,
            "vulkan",
            StringComparison.OrdinalIgnoreCase);
        if (!useVulkan && !string.Equals(
                configuration.SenseVoiceBackend,
                "cpu",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"不支持的 SenseVoice 引擎：{configuration.SenseVoiceBackend}。");
        }

        var executablePath = ResolveRequiredPath(
            useVulkan
                ? configuration.SenseVoiceVulkanExecutablePath
                : configuration.SenseVoiceExecutablePath,
            useVulkan ? "SenseVoice Vulkan 运行时" : "SenseVoice CPU 运行时");
        EnsureLanguageSelectionSupported(executablePath);
        var modelPath = ResolveRequiredPath(configuration.SenseVoiceModelPath, "SenseVoice 模型");
        string? vadPath = null;
        if (!string.IsNullOrWhiteSpace(configuration.SenseVoiceVadModelPath))
        {
            vadPath = ResolveRequiredPath(
                configuration.SenseVoiceVadModelPath,
                "SenseVoice VAD 模型");
        }
        var arguments = BuildArguments(configuration, modelPath, vadPath);

        _log.Info(
            $"[asr] 正在启动 SenseVoice 常驻进程：运行时={executablePath}，" +
            $"引擎={(useVulkan ? "Vulkan" : "CPU")}，" +
            $"GPU={configuration.SenseVoiceVulkanDeviceIndex?.ToString() ?? "None"}/" +
            $"{configuration.SenseVoiceVulkanDeviceName ?? "None"}，" +
            $"语言={configuration.EffectiveRecognitionLanguage}，" +
            $"模型={modelPath}，VAD={configuration.SenseVoiceVadModelPath}");
        _worker = new SenseVoiceResidentWorker(executablePath, arguments);
        _log.Info($"[asr] SenseVoice 常驻进程已就绪：PID={_worker.ProcessId}。");
    }

    public async Task<CommandTranscriptionResult> TranscribeAsync(
        CommandAudioInput audio,
        string diagnosticTag,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var size = new FileInfo(audio.FilePath).Length;
        _log.Info(
            $"{diagnosticTag} [asr] 请求开始：PID={_worker.ProcessId}，" +
            $"音频={audio.Duration.TotalMilliseconds:F0} ms/{size:N0} bytes，" +
            $"文件={Path.GetFileName(audio.FilePath)}");
        try
        {
            var text = await _worker.TranscribeAsync(audio.FilePath, cancellationToken);
            stopwatch.Stop();
            var workerDiagnostics = _worker.TakeDiagnostics();
            if (!string.IsNullOrWhiteSpace(workerDiagnostics))
            {
                _log.Info($"{diagnosticTag} [asr] worker诊断：{SingleLine(workerDiagnostics)}");
            }

            _log.Info(
                $"{diagnosticTag} [asr] 请求完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
                $"字符={text.Length}，文本={Preview(text)}");
            return new CommandTranscriptionResult(text, stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _log.Error(
                $"{diagnosticTag} [asr] 请求失败：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms。",
                exception);
            throw;
        }
    }

    public void Dispose() => _worker.Dispose();

    internal static IReadOnlyList<string> BuildArguments(
        SpeechConfiguration configuration,
        string modelPath,
        string? vadPath)
    {
        List<string> arguments = ["-m", modelPath];
        arguments.Add("--language");
        arguments.Add(configuration.EffectiveRecognitionLanguage);
        if (string.Equals(
                configuration.SenseVoiceBackend,
                "vulkan",
                StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add("--backend");
            arguments.Add("vulkan");
            if (configuration.SenseVoiceVulkanDeviceIndex is { } deviceIndex)
            {
                if (deviceIndex < 0)
                {
                    throw new InvalidOperationException("SenseVoice Vulkan 设备序号不能为负数。");
                }

                arguments.Add("--device");
                arguments.Add(deviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        if (!string.IsNullOrWhiteSpace(vadPath))
        {
            arguments.Add("--vad");
            arguments.Add(vadPath);
        }

        return arguments;
    }

    internal static bool HelpTextSupportsLanguageSelection(string? helpText) =>
        helpText?.Contains("--language", StringComparison.Ordinal) == true;

    private static void EnsureLanguageSelectionSupported(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--help");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                AppLocalization.Text("Validation.AsrLanguageRuntime"));
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                AppLocalization.Text("Validation.AsrLanguageRuntime"));
        }

        var helpText = standardOutput.GetAwaiter().GetResult() +
                       standardError.GetAwaiter().GetResult();
        if (!HelpTextSupportsLanguageSelection(helpText))
        {
            throw new InvalidOperationException(
                AppLocalization.Text("Validation.AsrLanguageRuntime"));
        }
    }

    private static string Preview(string text) =>
        text.Length <= 120 ? text : text[..120] + "...";

    private static string SingleLine(string text) =>
        string.Join(" | ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string ResolveRequiredPath(string configuredPath, string description)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException($"{description}路径未配置。");
        }

        var path = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到{description}。", path);
        }

        return path;
    }
}

public sealed record CommandTranscriptionResult(string Text, TimeSpan Duration);

internal sealed class SenseVoiceResidentWorker : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Process _process;
    private readonly object _stderrSync = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrPump;
    private bool _disposed;

    public int ProcessId => _process.Id;

    public SenseVoiceResidentWorker(string executablePath, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.ArgumentList.Add("--worker");

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 SenseVoice：{executablePath}");
        _stderrPump = PumpStandardErrorAsync();
        try
        {
            var ready = _process.StandardOutput.ReadLineAsync()
                .WaitAsync(StartupTimeout)
                .GetAwaiter()
                .GetResult();
            if (!string.Equals(ready, "READY", StringComparison.Ordinal))
            {
                throw WorkerFailure("SenseVoice 运行时不支持常驻 worker 协议。");
            }
        }
        catch
        {
            StopProcess(graceful: false);
            throw;
        }
    }

    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process.HasExited)
            {
                throw WorkerFailure("SenseVoice 常驻进程已经退出。");
            }

            lock (_stderrSync)
            {
                _stderr.Clear();
            }

            var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(audioPath)));
            try
            {
                await _process.StandardInput.WriteLineAsync($"TRANSCRIBE\t{encodedPath}".AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
                var response = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                if (response is null)
                {
                    throw WorkerFailure("SenseVoice 未返回转写结果。");
                }

                if (response.StartsWith("RESULT\t", StringComparison.Ordinal))
                {
                    var text = Decode(response[7..]).Trim();
                    return text.Length == 0
                        ? throw new NoSpeechRecognizedException("SenseVoice 没有识别到语音文本。")
                        : text;
                }

                if (response.StartsWith("ERROR\t", StringComparison.Ordinal))
                {
                    var error = Decode(response[6..]);
                    if (LooksLikeNoSpeech(error))
                    {
                        throw new NoSpeechRecognizedException("SenseVoice 没有识别到语音文本。");
                    }

                    throw WorkerFailure($"SenseVoice 识别失败：{error}");
                }

                throw WorkerFailure($"SenseVoice 返回了未知响应：{response}");
            }
            catch (OperationCanceledException)
            {
                StopProcess(graceful: false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public string TakeDiagnostics()
    {
        lock (_stderrSync)
        {
            var diagnostics = _stderr.ToString().Trim();
            _stderr.Clear();
            return diagnostics;
        }
    }

    internal static bool LooksLikeNoSpeech(string message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            message.Contains("read audio failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Contains("0 vad segments", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no transcription", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no speech", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("没有识别到语音", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess(graceful: true);
        _gate.Dispose();
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
            {
                lock (_stderrSync)
                {
                    _stderr.AppendLine(line);
                    if (_stderr.Length > 4000)
                    {
                        _stderr.Remove(0, _stderr.Length - 4000);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private InvalidOperationException WorkerFailure(string message)
    {
        string detail;
        lock (_stderrSync)
        {
            detail = _stderr.ToString().Trim();
        }

        var exit = _process.HasExited ? $" 退出代码={_process.ExitCode}。" : string.Empty;
        return new InvalidOperationException(
            detail.Length == 0 ? message + exit : $"{message}{exit} {detail}");
    }

    private void StopProcess(bool graceful)
    {
        try
        {
            if (!_process.HasExited && graceful)
            {
                _process.StandardInput.WriteLine("QUIT");
                _process.StandardInput.Flush();
            }
            else if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            if (!_process.HasExited && !_process.WaitForExit(2000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
        }
        finally
        {
            try
            {
                _stderrPump.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
            }
            _process.Dispose();
        }
    }

    private static string Decode(string encoded)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("SenseVoice 返回了无效的 Base64 文本。", exception);
        }
    }
}

public sealed class NoSpeechRecognizedException(string message) : InvalidOperationException(message);
