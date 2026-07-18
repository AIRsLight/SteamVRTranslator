using System.Diagnostics;
using SteamVRTranslator.App.Capture;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.App.Translation;

public sealed class TranslationPipeline : IDisposable
{
    private readonly AppConfiguration _configuration;
    private readonly AppLog _log;
    private readonly SteamVrCompositorCaptureService _captureService;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(90) };

    public TranslationPipeline(AppConfiguration configuration, AppLog log)
    {
        _configuration = configuration;
        _log = log;
        _captureService = new SteamVrCompositorCaptureService(log);
    }

    public async Task<TranslationResult> ExecuteAsync(
        SpatialSelectionPlane plane,
        AssistantRequestMode mode,
        string? customCommand,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken,
        string diagnosticTag = "")
    {
        var capture = await CaptureAsync(plane, cancellationToken, diagnosticTag);
        return await ExecuteAsync(
            capture,
            mode,
            customCommand,
            onPartialResult,
            cancellationToken,
            diagnosticTag);
    }

    public async Task<CapturedFrame> CaptureAsync(
        SpatialSelectionPlane plane,
        CancellationToken cancellationToken,
        string diagnosticTag = "")
    {
        var stopwatch = Stopwatch.StartNew();
        _log.Info(
            $"{diagnosticTag} [capture] 开始：捕获源=SteamVR Compositor，" +
            $"空间平面={plane.Width:F3}m x {plane.Height:F3}m，" +
            $"捕获眼睛={_configuration.StereoCompositionMode}");
        await Task.Delay(80, cancellationToken);
        var image = _captureService.CaptureJpeg(
            plane,
            _configuration.StereoCompositionMode,
            diagnosticTag);
        var captureDirectory = Path.IsPathRooted(_configuration.CaptureDirectory)
            ? _configuration.CaptureDirectory
            : Path.Combine(AppContext.BaseDirectory, _configuration.CaptureDirectory);
        Directory.CreateDirectory(captureDirectory);
        var path = Path.Combine(captureDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
        await File.WriteAllBytesAsync(path, image, cancellationToken);
        stopwatch.Stop();
        _log.Info(
            $"{diagnosticTag} [capture] 完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
            $"JPEG={image.Length:N0} bytes，文件={path}");
        return new CapturedFrame(path, image);
    }

    public async Task<TranslationResult> ExecuteAsync(
        CapturedFrame capture,
        AssistantRequestMode mode,
        string? customCommand,
        Action<string>? onPartialResult,
        CancellationToken cancellationToken,
        string diagnosticTag = "")
    {
        var stopwatch = Stopwatch.StartNew();
        var partialCount = 0;
        var partialLength = 0;
        var partialStopwatch = Stopwatch.StartNew();
        void ReportPartial(string partial)
        {
            partialCount++;
            partialLength = partial.Length;
            if (partialCount == 1 || partialCount % 10 == 0)
            {
                _log.Info(
                    $"{diagnosticTag} [stream] 分块={partialCount}，" +
                    $"累计字符={partialLength}，耗时={partialStopwatch.Elapsed.TotalMilliseconds:F0} ms");
            }
            onPartialResult?.Invoke(partial);
        }

        _log.Info(
            $"{diagnosticTag} [backend] 开始：模式={mode}，JPEG={capture.ImageBytes.Length:N0} bytes，" +
            $"Provider={(mode == AssistantRequestMode.Translate ? ActiveProviderDescription() : "mock-command-repeat")}");
        var image = capture.ImageBytes;

        string? text;
        if (mode == AssistantRequestMode.CustomCommand)
        {
            var command = customCommand?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException("SenseVoice 没有返回可用的自定义命令。");
            }

            ICustomCommandBackend backend = new MockCustomCommandBackend();
            _log.Info(
                $"{diagnosticTag} [backend] 调用自定义命令后端：{backend.GetType().Name}，" +
                $"命令字符={command.Length}，命令={Preview(command)}");
            text = await backend.ExecuteAsync(image, command, ReportPartial, cancellationToken);
        }
        else
        {
            var backend = CreateBackend();
            _log.Info($"{diagnosticTag} [backend] 调用翻译后端：{backend.GetType().Name}");
            text = await backend.TranslateAsync(image, ReportPartial, cancellationToken);
        }

        stopwatch.Stop();
        _log.Info(
            $"{diagnosticTag} [backend] 完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
            $"流式分块={partialCount}，累计字符={partialLength}，最终字符={text?.Length ?? 0}，" +
            $"预览={Preview(text)}");

        return new TranslationResult(capture.Path, text, mode, customCommand);
    }

    public void Dispose()
    {
        _captureService.Dispose();
        _httpClient.Dispose();
    }

    public void ReleaseOpenVrResources() =>
        _captureService.ReleaseOpenVrResources();

    private ITranslationBackend CreateBackend()
    {
        var provider = _configuration.Translation.GetActiveProvider()
            ?? throw new InvalidOperationException("当前启用的翻译提供商不存在。");
        if (provider.IsMock)
        {
            return new MockTranslationBackend();
        }

        if (string.Equals(
                provider.Type,
                TranslationProviderConfiguration.OpenAiCompatibleType,
                StringComparison.OrdinalIgnoreCase))
        {
            return new OpenAiCompatibleVisionBackend(
                _configuration.Translation,
                _httpClient);
        }

        throw new InvalidOperationException($"不支持的翻译提供商类型：{provider.Type}");
    }

    private string ActiveProviderDescription()
    {
        var provider = _configuration.Translation.GetActiveProvider();
        return provider is null ? "<缺失>" : $"{provider.DisplayName} ({provider.Type})";
    }

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<空>";
        }

        var singleLine = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 160 ? singleLine : singleLine[..160] + "...";
    }
}

public enum AssistantRequestMode
{
    Translate,
    CustomCommand
}

public sealed record CapturedFrame(string Path, byte[] ImageBytes);

public sealed record TranslationResult(
    string CapturePath,
    string? Text,
    AssistantRequestMode Mode,
    string? CustomCommand);
