using System.Diagnostics;
using System.Windows.Media.Imaging;
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
    private readonly ProviderConcurrencyLimiter _providerConcurrency = new();
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string _stereoCompositionMode;
    private TranslationConfiguration _translationConfiguration;
    private string _customCommandSystemPrompt;
    private string _customCommandPrompt;

    public TranslationPipeline(AppConfiguration configuration, AppLog log)
    {
        _configuration = configuration;
        _log = log;
        _captureService = new SteamVrCompositorCaptureService(log);
        _stereoCompositionMode = configuration.StereoCompositionMode;
        _translationConfiguration = configuration.Translation;
        _providerConcurrency.UpdateLimits(configuration.Translation.Providers);
        _customCommandSystemPrompt = configuration.Speech.CustomCommandSystemPrompt;
        _customCommandPrompt = configuration.Speech.CustomCommandPrompt;
    }

    public void ApplyLiveSettings(
        string stereoCompositionMode,
        TranslationConfiguration translationConfiguration,
        string customCommandSystemPrompt,
        string customCommandPrompt)
    {
        ArgumentNullException.ThrowIfNull(translationConfiguration);
        _providerConcurrency.UpdateLimits(translationConfiguration.Providers);
        Volatile.Write(ref _stereoCompositionMode, stereoCompositionMode);
        Volatile.Write(ref _translationConfiguration, translationConfiguration);
        Volatile.Write(ref _customCommandSystemPrompt, customCommandSystemPrompt);
        Volatile.Write(ref _customCommandPrompt, customCommandPrompt);
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
            Array.Empty<AssistantConversationTurn>(),
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
        var stereoCompositionMode = Volatile.Read(ref _stereoCompositionMode);
        _log.Info(
            $"{diagnosticTag} [capture] 开始：捕获源=SteamVR Compositor，" +
            $"空间平面={plane.Width:F3}m x {plane.Height:F3}m，" +
            $"捕获眼睛={stereoCompositionMode}");
        await Task.Delay(80, cancellationToken);
        var image = _captureService.CaptureJpeg(
            plane,
            stereoCompositionMode,
            diagnosticTag);
        var captureDirectory = ApplicationDataPaths.ResolveCaptureDirectory(
            _configuration.CaptureDirectory);
        Directory.CreateDirectory(captureDirectory);
        var path = Path.Combine(captureDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
        await File.WriteAllBytesAsync(path, image, cancellationToken);
        var (pixelWidth, pixelHeight) = ReadImageDimensions(image);
        stopwatch.Stop();
        _log.Info(
            $"{diagnosticTag} [capture] 完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
            $"JPEG={image.Length:N0} bytes，分辨率={pixelWidth}x{pixelHeight}，文件={path}");
        return new CapturedFrame(path, image, pixelWidth, pixelHeight);
    }

    public async Task<TranslationResult> ExecuteAsync(
        CapturedFrame capture,
        AssistantRequestMode mode,
        string? customCommand,
        IReadOnlyList<AssistantConversationTurn> conversationHistory,
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

        var translationConfiguration = Volatile.Read(ref _translationConfiguration);
        var providerPurpose = mode switch
        {
            AssistantRequestMode.LayoutTranslate => PromptProviderPurpose.LayoutTranslation,
            AssistantRequestMode.CustomCommand => PromptProviderPurpose.CustomCommand,
            _ => PromptProviderPurpose.DirectTranslation
        };
        var provider = translationConfiguration.GetProviderFor(providerPurpose)
            ?? throw new InvalidOperationException("当前请求没有可用的翻译提供商。");
        _log.Info(
            $"{diagnosticTag} [backend] 开始：模式={mode}，JPEG={capture.ImageBytes.Length:N0} bytes，" +
            $"Provider={ProviderDescription(provider)}，" +
            $"最大并发={provider.MaxConcurrency}");
        var image = capture.ImageBytes;
        var queueStopwatch = Stopwatch.StartNew();
        using var concurrencyLease = await _providerConcurrency.AcquireAsync(
            provider.Id,
            provider.MaxConcurrency,
            cancellationToken);
        queueStopwatch.Stop();
        if (queueStopwatch.Elapsed >= TimeSpan.FromMilliseconds(10))
        {
            _log.Info(
                $"{diagnosticTag} [backend] Provider 并发槽已取得：" +
                $"Provider={provider.Id}，等待={queueStopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
                $"上限={provider.MaxConcurrency}");
        }
        var backend = CreateBackend(translationConfiguration, provider);

        string? text;
        ResultContentFormat contentFormat;
        if (mode == AssistantRequestMode.CustomCommand)
        {
            var command = customCommand?.Trim();
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException("SenseVoice 没有返回可用的自定义命令。");
            }

            _log.Info(
                $"{diagnosticTag} [backend] 调用自定义命令后端：{backend.GetType().Name}，" +
                $"命令字符={command.Length}，命令={Preview(command)}");
            text = await backend.ExecuteCustomCommandAsync(
                image,
                command,
                conversationHistory,
                ReportPartial,
                cancellationToken);
            contentFormat = ResultContentFormat.Markdown;
        }
        else
        {
            if (mode == AssistantRequestMode.LayoutTranslate)
            {
                _log.Info($"{diagnosticTag} [backend] 调用排版翻译后端：{backend.GetType().Name}");
                text = await backend.TranslateLayoutAsync(image, cancellationToken);
                contentFormat = ResultContentFormat.Html;
            }
            else
            {
                _log.Info($"{diagnosticTag} [backend] 调用 Markdown 翻译后端：{backend.GetType().Name}");
                text = await backend.TranslateAsync(image, ReportPartial, cancellationToken);
                contentFormat = ResultContentFormat.Markdown;
            }
        }

        stopwatch.Stop();
        _log.Info(
            $"{diagnosticTag} [backend] 完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
            $"流式分块={partialCount}，累计字符={partialLength}，最终字符={text?.Length ?? 0}，" +
            $"预览={Preview(text)}");

        return new TranslationResult(
            capture.Path,
            text,
            mode,
            customCommand,
            contentFormat,
            capture.PixelWidth,
            capture.PixelHeight);
    }

    public async Task<string> TranslateSpeechAsync(
        string sourceText,
        string targetLanguage,
        string systemPrompt,
        string taskPrompt,
        CancellationToken cancellationToken,
        string diagnosticTag = "")
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new InvalidOperationException("SenseVoice 没有返回可翻译的文本。");
        }

        var stopwatch = Stopwatch.StartNew();
        var translationConfiguration = Volatile.Read(ref _translationConfiguration);
        var provider = translationConfiguration.GetProviderFor(PromptProviderPurpose.VoiceTranslation)
            ?? throw new InvalidOperationException("语音翻译没有可用的模型提供商。");
        _log.Info(
            $"{diagnosticTag} [voice-translation] 开始：Provider={ProviderDescription(provider)}，" +
            $"目标语言={targetLanguage}，原文字符={sourceText.Length}，最大并发={provider.MaxConcurrency}。");
        using var concurrencyLease = await _providerConcurrency.AcquireAsync(
            provider.Id,
            provider.MaxConcurrency,
            cancellationToken);
        var backend = CreateBackend(translationConfiguration, provider);
        var translated = await backend.TranslateTextAsync(
            sourceText,
            targetLanguage,
            systemPrompt,
            taskPrompt,
            onPartialResult: null,
            cancellationToken);
        translated = translated?.Trim();
        if (string.IsNullOrWhiteSpace(translated))
        {
            throw new InvalidOperationException("语音翻译模型没有返回译文。");
        }

        stopwatch.Stop();
        _log.Info(
            $"{diagnosticTag} [voice-translation] 完成：耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms，" +
            $"译文字符={translated.Length}，预览={Preview(translated)}");
        return translated;
    }

    public void Dispose()
    {
        _captureService.Dispose();
        _httpClient.Dispose();
    }

    public void ReleaseOpenVrResources() =>
        _captureService.ReleaseOpenVrResources();

    private ITranslationBackend CreateBackend(
        TranslationConfiguration translationConfiguration,
        TranslationProviderConfiguration provider,
        PromptProviderPurpose textTranslationPurpose = PromptProviderPurpose.VoiceTranslation)
    {
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
                translationConfiguration,
                _httpClient,
                Volatile.Read(ref _customCommandSystemPrompt),
                Volatile.Read(ref _customCommandPrompt),
                providerId: provider.Id,
                textTranslationPurpose: textTranslationPurpose);
        }

        throw new InvalidOperationException($"不支持的翻译提供商类型：{provider.Type}");
    }

    private static string ProviderDescription(TranslationProviderConfiguration provider) =>
        $"{provider.DisplayName} ({provider.Type})";

    private static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<空>";
        }

        var singleLine = string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 160 ? singleLine : singleLine[..160] + "...";
    }

    private static (int Width, int Height) ReadImageDimensions(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}

public enum AssistantRequestMode
{
    Translate,
    LayoutTranslate,
    CustomCommand
}

public enum ResultContentFormat
{
    PlainText,
    Markdown,
    Html
}

public sealed record CapturedFrame(
    string Path,
    byte[] ImageBytes,
    int PixelWidth,
    int PixelHeight);

public sealed record TranslationResult(
    string CapturePath,
    string? Text,
    AssistantRequestMode Mode,
    string? CustomCommand,
    ResultContentFormat ContentFormat,
    int SourcePixelWidth,
    int SourcePixelHeight);
