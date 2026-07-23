using System.Text.Json.Serialization;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Configuration;

public sealed class AppConfiguration
{
    public const int DefaultPointerSmoothingStrength = 50;

    public AppConfiguration()
    {
        ApplyPromptLanguage();
    }

    public string UiLanguage { get; set; } = ApplicationLanguages.English;

    public int HotKeyVirtualKey { get; set; } = 0x77;

    public int SelectionTimeoutSeconds { get; set; } = 30;

    public string CaptureDirectory { get; set; } = "captures";

    public string StereoCompositionMode { get; set; } = "left-eye";

    public bool InvertResultScroll { get; set; }

    public bool ShowPointerRay { get; set; }

    public int PointerSmoothingStrength { get; set; } = DefaultPointerSmoothingStrength;

    public static int NormalizePointerSmoothingStrength(int value) =>
        Math.Clamp(value, 0, 100);

    public Dictionary<string, LocalizedPromptConfiguration> Prompts { get; set; } =
        BuiltInPromptDefaults.CreateAll();

    public TranslationConfiguration Translation { get; set; } = new();

    public SpeechConfiguration Speech { get; set; } = new();

    public VrChatVoiceInputConfiguration VrChatVoiceInput { get; set; } = new();

    public SubtitleConfiguration Subtitles { get; set; } = new();

    public AndroidMirrorConfiguration AndroidMirror { get; set; } = new();

    public LocalizedPromptConfiguration GetPromptSet(string? language = null)
    {
        var normalizedLanguage = ApplicationLanguages.Normalize(language ?? UiLanguage);
        if (!Prompts.TryGetValue(normalizedLanguage, out var prompts))
        {
            prompts = BuiltInPromptDefaults.Create(normalizedLanguage);
            Prompts[normalizedLanguage] = prompts;
        }

        return prompts;
    }

    public void ApplyPromptLanguage()
    {
        var prompts = GetPromptSet();
        Translation.SystemPrompt = prompts.MarkdownSystemPrompt;
        Translation.MarkdownTranslationPrompt = prompts.MarkdownTranslationPrompt;
        Translation.LayoutTranslationSystemPrompt = prompts.LayoutSystemPrompt;
        Translation.LayoutTranslationPrompt = prompts.LayoutTranslationPrompt;
        Speech.CustomCommandSystemPrompt = prompts.CustomCommandSystemPrompt;
        Speech.CustomCommandPrompt = prompts.CustomCommandPrompt;
        VrChatVoiceInput.TranslationSystemPrompt = prompts.VoiceTranslationSystemPrompt;
        VrChatVoiceInput.TranslationPrompt = prompts.VoiceTranslationPrompt;
        Subtitles.TranslationSystemPrompt = prompts.SubtitleTranslationSystemPrompt;
        Subtitles.TranslationPrompt = prompts.SubtitleTranslationPrompt;
    }
}

public sealed class AndroidMirrorConfiguration
{
    public const int MinimumSize = 720;

    public const int MaximumAllowedSize = 1080;

    public const double DefaultWindowWidthMeters = 0.132;

    public const double DefaultWindowLongSideMeters = 0.28;

    public const double DefaultWindowScale = 1.0;

    public static readonly double[] SupportedWindowScales = [0.75, 1.0, 1.25, 1.5];

    public static readonly int[] SupportedSizes = [720, 900, 1080];

    public static readonly int[] SupportedFrameRates = [30, 60, 90, 120];

    public bool Enabled { get; set; }

    public string DeviceSerial { get; set; } = string.Empty;

    public int MaximumSize { get; set; } = MinimumSize;

    public int MaximumFramesPerSecond { get; set; } = 30;

    public int VideoBitRateMbps { get; set; } = 4;

    public string VideoDecoder { get; set; } = AndroidVideoDecoders.Auto;

    public double WindowWidthMeters { get; set; } = DefaultWindowWidthMeters;

    public static int NormalizeMaximumSize(int value) => SupportedSizes
        .OrderBy(candidate => Math.Abs(candidate - value))
        .ThenBy(candidate => candidate)
        .First();

    public static int NormalizeMaximumFramesPerSecond(int value) => value switch
    {
        >= 105 => 120,
        >= 75 => 90,
        >= 45 => 60,
        _ => 30
    };

    public static double NormalizeWindowWidthMeters(double value)
    {
        if (!double.IsFinite(value) ||
            Math.Abs(value - 0.113) < 0.002 ||
            value < 0.09 ||
            value > 0.20)
        {
            return DefaultWindowWidthMeters;
        }

        return Math.Clamp(value, 0.09, 0.20);
    }

    public static double NormalizeWindowScale(double value) => SupportedWindowScales
        .OrderBy(candidate => Math.Abs(candidate - value))
        .ThenBy(candidate => candidate)
        .First();

    public static double WindowWidthFromScale(double scale) =>
        DefaultWindowWidthMeters * NormalizeWindowScale(scale);

    public static double WindowScaleFromWidth(double widthMeters) =>
        NormalizeWindowScale(NormalizeWindowWidthMeters(widthMeters) / DefaultWindowWidthMeters);
}

public static class AndroidVideoDecoders
{
    public const string Auto = "auto";
    public const string Software = "software";
    public const string D3D11 = "d3d11va";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Software => Software,
        D3D11 => D3D11,
        _ => Auto
    };
}

public sealed class TranslationConfiguration
{
    public string ActiveProviderId { get; set; } = TranslationProviderConfiguration.MockProviderId;

    public List<TranslationProviderConfiguration> Providers { get; set; } =
    [
        TranslationProviderConfiguration.CreateMock()
    ];

    public string TargetLanguage { get; set; } = "zh-CN";

    public bool EnableStreaming { get; set; } = true;

    public bool DisableThinking { get; set; } = true;

    public PromptProviderConfiguration PromptProviders { get; set; } = new();

    [JsonIgnore]
    public string SystemPrompt { get; set; } = BuiltInPromptDefaults.MarkdownSystemPrompt;

    [JsonIgnore]
    public string MarkdownTranslationPrompt { get; set; } =
        BuiltInPromptDefaults.MarkdownTranslationPrompt;

    [JsonIgnore]
    public string LayoutTranslationSystemPrompt { get; set; } =
        BuiltInPromptDefaults.LayoutSystemPrompt;

    [JsonIgnore]
    public string LayoutTranslationPrompt { get; set; } =
        BuiltInPromptDefaults.LayoutTranslationPrompt;

    public TranslationProviderConfiguration? GetActiveProvider() =>
        Providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, ActiveProviderId, StringComparison.OrdinalIgnoreCase));

    public TranslationProviderConfiguration? GetProviderFor(PromptProviderPurpose purpose)
    {
        var providerId = PromptProviders.GetProviderId(purpose);
        return string.IsNullOrWhiteSpace(providerId)
            ? GetActiveProvider()
            : Providers.FirstOrDefault(provider =>
                  string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase))
              ?? GetActiveProvider();
    }
}

public enum PromptProviderPurpose
{
    DirectTranslation,
    LayoutTranslation,
    CustomCommand,
    VoiceTranslation,
    SubtitleTranslation
}

public sealed class PromptProviderConfiguration
{
    public string? DirectTranslationProviderId { get; set; }

    public string? LayoutTranslationProviderId { get; set; }

    public string? CustomCommandProviderId { get; set; }

    public string? VoiceTranslationProviderId { get; set; }

    public string? SubtitleTranslationProviderId { get; set; }

    public string? GetProviderId(PromptProviderPurpose purpose) => purpose switch
    {
        PromptProviderPurpose.LayoutTranslation => LayoutTranslationProviderId,
        PromptProviderPurpose.CustomCommand => CustomCommandProviderId,
        PromptProviderPurpose.VoiceTranslation => VoiceTranslationProviderId,
        PromptProviderPurpose.SubtitleTranslation => SubtitleTranslationProviderId,
        _ => DirectTranslationProviderId
    };

    public void SetProviderId(PromptProviderPurpose purpose, string? providerId)
    {
        var normalized = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim();
        switch (purpose)
        {
            case PromptProviderPurpose.LayoutTranslation:
                LayoutTranslationProviderId = normalized;
                break;
            case PromptProviderPurpose.CustomCommand:
                CustomCommandProviderId = normalized;
                break;
            case PromptProviderPurpose.VoiceTranslation:
                VoiceTranslationProviderId = normalized;
                break;
            case PromptProviderPurpose.SubtitleTranslation:
                SubtitleTranslationProviderId = normalized;
                break;
            default:
                DirectTranslationProviderId = normalized;
                break;
        }
    }

    public PromptProviderConfiguration Clone() => new()
    {
        DirectTranslationProviderId = DirectTranslationProviderId,
        LayoutTranslationProviderId = LayoutTranslationProviderId,
        CustomCommandProviderId = CustomCommandProviderId,
        VoiceTranslationProviderId = VoiceTranslationProviderId,
        SubtitleTranslationProviderId = SubtitleTranslationProviderId
    };
}

public sealed class TranslationProviderConfiguration
{
    public const int DefaultMaxConcurrency = 2;
    public const int MaximumMaxConcurrency = 16;
    public const string MockProviderId = "mock";
    public const string MockType = "mock";
    public const string OpenAiCompatibleType = "openai-compatible";

    public string Id { get; set; } = "openai";

    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = OpenAiCompatibleType;

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int MaxConcurrency { get; set; } = DefaultMaxConcurrency;

    [JsonIgnore]
    public bool IsMock => string.Equals(Type, MockType, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (IsMock)
            {
                return AppLocalization.Text("Provider.Mock.Name");
            }

            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name.Trim();
            }

            return SuggestedName(BaseUrl, Model, IsMock);
        }
    }

    public static string SuggestedName(string? baseUrl, string? model, bool isMock = false)
    {
        if (isMock)
        {
            return "模拟提供商";
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrWhiteSpace(model) ? uri.Host : $"{uri.Host} · {model}";
        }

        return "未命名提供商";
    }

    public static TranslationProviderConfiguration CreateMock() => new()
    {
        Id = MockProviderId,
        Name = string.Empty,
        Type = MockType,
        BaseUrl = string.Empty,
        ApiKey = string.Empty,
        Model = string.Empty,
        MaxConcurrency = DefaultMaxConcurrency
    };

    public override string ToString() => DisplayName;
}

public sealed class SpeechConfiguration
{
    public string DeviceId { get; set; } = string.Empty;

    public int HoldThresholdMilliseconds { get; set; } = 350;

    public int MinimumDurationMilliseconds { get; set; } = 250;

    public int MaximumDurationSeconds { get; set; } = 15;

    public string SenseVoiceExecutablePath { get; set; } = "runtimes/llama-funasr-sensevoice.exe";

    public string SenseVoiceVulkanExecutablePath { get; set; } =
        "runtimes/sensevoice-vulkan/llama-funasr-sensevoice.exe";

    public string SenseVoiceBackend { get; set; } = "cpu";

    public int? SenseVoiceVulkanDeviceIndex { get; set; }

    public string? SenseVoiceVulkanDeviceName { get; set; }

    public string SenseVoiceModelPath { get; set; } = "models/sensevoice-small-q8.gguf";

    public string? SenseVoiceVadModelPath { get; set; } = "models/fsmn-vad.gguf";

    public string RecognitionLanguage { get; set; } = SpeechRecognitionLanguages.FollowInterface;

    [JsonIgnore]
    public string EffectiveRecognitionLanguage { get; set; } = SpeechRecognitionLanguages.Automatic;

    [JsonIgnore]
    public string CustomCommandSystemPrompt { get; set; } =
        BuiltInPromptDefaults.CustomCommandSystemPrompt;

    [JsonIgnore]
    public string CustomCommandPrompt { get; set; } = BuiltInPromptDefaults.CustomCommandPrompt;
}

public sealed class LocalizedPromptConfiguration
{
    public string MarkdownSystemPrompt { get; set; } = string.Empty;

    public string MarkdownTranslationPrompt { get; set; } = string.Empty;

    public string LayoutSystemPrompt { get; set; } = string.Empty;

    public string LayoutTranslationPrompt { get; set; } = string.Empty;

    public string CustomCommandSystemPrompt { get; set; } = string.Empty;

    public string CustomCommandPrompt { get; set; } = string.Empty;

    public string VoiceTranslationSystemPrompt { get; set; } = string.Empty;

    public string VoiceTranslationPrompt { get; set; } = string.Empty;

    public string SubtitleTranslationSystemPrompt { get; set; } = string.Empty;

    public string SubtitleTranslationPrompt { get; set; } = string.Empty;

    public LocalizedPromptConfiguration Clone() => new()
    {
        MarkdownSystemPrompt = MarkdownSystemPrompt,
        MarkdownTranslationPrompt = MarkdownTranslationPrompt,
        LayoutSystemPrompt = LayoutSystemPrompt,
        LayoutTranslationPrompt = LayoutTranslationPrompt,
        CustomCommandSystemPrompt = CustomCommandSystemPrompt,
        CustomCommandPrompt = CustomCommandPrompt,
        VoiceTranslationSystemPrompt = VoiceTranslationSystemPrompt,
        VoiceTranslationPrompt = VoiceTranslationPrompt,
        SubtitleTranslationSystemPrompt = SubtitleTranslationSystemPrompt,
        SubtitleTranslationPrompt = SubtitleTranslationPrompt
    };
}

public static class BuiltInPromptDefaults
{
    internal const string LegacyLayoutTranslationPrompt =
        "按原图的位置、层级、字号、对齐和色彩关系重建版面。" +
        "译文中每段文字的字符视觉高度、行高、粗细和占用比例必须与原图对应文字一致；不得统一使用小字号，" +
        "也不得为了容纳较长译文而整体缩小字体，优先换行并在局部调整字间距或布局。" +
        "字号、间距和定位优先使用 vw、vh、百分比等随视口等比缩放的 CSS 单位，" +
        "使页面在 2048 像素长边的浏览器视口中仍与原图同样清晰可读。" +
        "只返回一个完整的单文件 HTML 文档，不要使用 Markdown 代码围栏。" +
        "所有 CSS 必须内联在文档中；禁止脚本、表单、外部链接、外部图片、外部字体和任何网络资源。" +
        "页面必须自适应给定浏览器视口，html 与 body 使用完整宽高且不产生滚动条。" +
        "没有可见文字时，在完整 HTML 文档中显示 [NO_TEXT]。";

    public const string MarkdownSystemPrompt =
        "你是 VR 画面翻译助手。识别截图中的可见文字并翻译为指定目标语言。" +
        "只输出自然、可直接阅读的译文，保留必要的换行和上下文。不要解释翻译过程，也不要输出 JSON。";

    public const string MarkdownTranslationPrompt =
        "识别截图中的全部可见文字并翻译。使用 Markdown 排版结果，可以使用标题、列表、引用、表格、强调和代码。" +
        "不要输出 HTML，不要嵌入图片或外部资源。没有可见文字时返回 [NO_TEXT]。";

    public const string LayoutSystemPrompt =
        "你是 VR 画面翻译与版面重建助手。准确识别截图中的可见文字，将其翻译为指定目标语言，" +
        "并生成安全、完整、可直接渲染的单文件 HTML。不要解释处理过程。";

    public const string LayoutTranslationPrompt =
        "只重建承载主要可读内容的主体元素，例如海报、封面、书页、标签、报纸、菜单、告示、控制面板或屏幕界面。" +
        "忽略人物、房间、墙面、家具、风景、光影等与文字主体无关的背景，不要在 HTML 中复现这些背景。" +
        "若画面中有多个候选主体，只重建框选区域内最主要、最完整且与文字相关的一个。" +
        "按主体元素在原图中的位置、层级、字号、对齐和色彩关系重建版面。" +
        "译文中每段文字的字符视觉高度、行高、粗细和占用比例必须与原图对应文字一致；不得统一使用小字号，" +
        "也不得为了容纳较长译文而整体缩小字体，优先换行并在局部调整字间距或布局。" +
        "字号、间距和定位优先使用 vw、vh、百分比等随视口等比缩放的 CSS 单位，" +
        "使页面在 2048 像素长边的浏览器视口中仍与原图同样清晰可读。" +
        "只返回一个完整的单文件 HTML 文档，不要使用 Markdown 代码围栏。" +
        "所有 CSS 必须内联在文档中；禁止脚本、表单、外部链接、外部图片、外部字体和任何网络资源。" +
        "页面必须自适应给定浏览器视口，html 与 body 使用完整宽高且不产生滚动条。" +
        "没有可见文字时，在完整 HTML 文档中显示 [NO_TEXT]。";

    public const string CustomCommandSystemPrompt =
        "你是 VR 视觉助手。结合截图和用户语音转写指令回答。优先依据截图中实际可见的信息；" +
        "不确定时明确说明。直接输出适合 VR 叠加层阅读的自然文本，不要输出 JSON 或代码块。";

    public const string CustomCommandPrompt =
        "结合附带的 VR 截图执行用户命令。使用 Markdown 排版回答；" +
        "不要输出 HTML、JSON、代码围栏、图片或外部资源。除非回答本身需要，否则不要复述命令。";

    public const string VoiceTranslationSystemPrompt =
        "你是实时语音翻译助手。将语音识别得到的文本准确翻译为指定目标语言。" +
        "只输出可以直接发送的自然译文，不要解释、不要复述原文，不要输出 Markdown、JSON 或标签。";

    public const string VoiceTranslationPrompt =
        "翻译下面的语音转写文本。保留原意、语气、专有名词和必要标点；" +
        "不要添加原文中不存在的信息。";

    public const string SubtitleTranslationSystemPrompt =
        "你是实时字幕翻译助手。输入来自广播、旁白、视频或多人对话的语音识别文本。" +
        "准确翻译为指定目标语言，只输出自然、简洁且适合字幕阅读的译文，不要解释，不要输出标签或 Markdown。";

    public const string SubtitleTranslationPrompt =
        "翻译下面这段字幕。保留说话语气、专有名词和上下文，不要补充原文中不存在的信息。";

    public static Dictionary<string, LocalizedPromptConfiguration> CreateAll() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [ApplicationLanguages.Chinese] = Create(ApplicationLanguages.Chinese),
            [ApplicationLanguages.English] = Create(ApplicationLanguages.English),
            [ApplicationLanguages.Japanese] = Create(ApplicationLanguages.Japanese)
        };

    public static LocalizedPromptConfiguration Create(string? language) =>
        ApplicationLanguages.Normalize(language) switch
        {
            ApplicationLanguages.Chinese => new LocalizedPromptConfiguration
            {
                MarkdownSystemPrompt = MarkdownSystemPrompt,
                MarkdownTranslationPrompt = MarkdownTranslationPrompt,
                LayoutSystemPrompt = LayoutSystemPrompt,
                LayoutTranslationPrompt = LayoutTranslationPrompt,
                CustomCommandSystemPrompt = CustomCommandSystemPrompt,
                CustomCommandPrompt = CustomCommandPrompt,
                VoiceTranslationSystemPrompt = VoiceTranslationSystemPrompt,
                VoiceTranslationPrompt = VoiceTranslationPrompt,
                SubtitleTranslationSystemPrompt = SubtitleTranslationSystemPrompt,
                SubtitleTranslationPrompt = SubtitleTranslationPrompt
            },
            ApplicationLanguages.Japanese => CreateJapanese(),
            _ => CreateEnglish()
        };

    private static LocalizedPromptConfiguration CreateEnglish() => new()
    {
        MarkdownSystemPrompt =
            "You are a VR screen translation assistant. Recognize visible text in the screenshot and translate it into the requested target language. " +
            "Output only a natural, directly readable translation. Preserve meaningful line breaks and context. Do not explain the process or output JSON.",
        MarkdownTranslationPrompt =
            "Recognize and translate all visible text in the screenshot. Format the result as Markdown; headings, lists, quotations, tables, emphasis, and code are allowed. " +
            "Do not output HTML or embed images or external resources. Return [NO_TEXT] when no visible text is present.",
        LayoutSystemPrompt =
            "You are a VR screen translation and layout reconstruction assistant. Accurately recognize visible text, translate it into the requested target language, " +
            "and produce a safe, complete, directly renderable single-file HTML document. Do not explain the process.",
        LayoutTranslationPrompt =
            "Reconstruct only the primary element that carries readable content, such as a poster, cover, book page, label, newspaper, menu, notice, control panel, or screen interface. " +
            "Ignore people, rooms, walls, furniture, scenery, lighting, and other background unrelated to the text-bearing subject; do not reproduce that background in HTML. " +
            "If several candidate subjects are visible, reconstruct only the most prominent, complete, text-related subject inside the selected region. " +
            "Match the subject's original position, hierarchy, font sizes, alignment, and color relationships. Each translated text block must retain the visual character height, line height, weight, and occupied proportion of its source. " +
            "Do not use one small font size for everything or shrink all text to fit longer translations; prefer wrapping and local spacing or layout adjustments. " +
            "Prefer viewport-relative CSS units such as vw, vh, and percentages so the page remains equally clear in a browser viewport with a 2048-pixel long edge. " +
            "Return exactly one complete single-file HTML document without Markdown fences. Inline all CSS. Scripts, forms, external links, images, fonts, and network resources are forbidden. " +
            "The page must fit the supplied viewport, with html and body using the full size and no scrollbars. If no visible text exists, show [NO_TEXT] inside a complete HTML document.",
        CustomCommandSystemPrompt =
            "You are a VR visual assistant. Answer the user's transcribed voice instruction using the screenshot. Prefer information actually visible in the image and state uncertainty clearly. " +
            "Output natural text suitable for a VR overlay, without JSON or code fences.",
        CustomCommandPrompt =
            "Carry out the user's command using the attached VR screenshot. Format the answer as Markdown. Do not output HTML, JSON, code fences, images, or external resources. " +
            "Do not repeat the command unless the answer requires it.",
        VoiceTranslationSystemPrompt =
            "You are a real-time speech translation assistant. Accurately translate ASR text into the requested target language. " +
            "Output only the natural translation that can be sent directly. Do not explain, repeat the source, or output Markdown, JSON, or labels.",
        VoiceTranslationPrompt =
            "Translate the following speech transcription. Preserve its meaning, tone, proper nouns, and necessary punctuation. " +
            "Do not add information that is absent from the source.",
        SubtitleTranslationSystemPrompt =
            "You are a real-time subtitle translation assistant. The input may come from announcements, narration, videos, or multi-speaker dialogue. " +
            "Translate it accurately into the requested target language. Output only concise, natural text suitable for subtitles, without explanations, labels, or Markdown.",
        SubtitleTranslationPrompt =
            "Translate the following subtitle segment. Preserve tone, proper nouns, and conversational context. Do not add information absent from the source."
    };

    private static LocalizedPromptConfiguration CreateJapanese() => new()
    {
        MarkdownSystemPrompt =
            "あなたは VR 画面の翻訳アシスタントです。スクリーンショット内の見える文字を認識し、指定された対象言語へ翻訳してください。" +
            "自然でそのまま読める訳文だけを出力し、必要な改行と文脈を保ってください。処理手順の説明や JSON は出力しないでください。",
        MarkdownTranslationPrompt =
            "スクリーンショット内の見える文字をすべて認識して翻訳してください。結果は Markdown で整形し、見出し、リスト、引用、表、強調、コードを使用できます。" +
            "HTML、画像、外部リソースは出力しないでください。見える文字がない場合は [NO_TEXT] を返してください。",
        LayoutSystemPrompt =
            "あなたは VR 画面の翻訳およびレイアウト再構築アシスタントです。見える文字を正確に認識して指定された対象言語へ翻訳し、" +
            "安全で完全な、そのまま描画できる単一 HTML ファイルを生成してください。処理手順は説明しないでください。",
        LayoutTranslationPrompt =
            "ポスター、表紙、書籍のページ、ラベル、新聞、メニュー、掲示、操作パネル、画面 UI など、読み取れる内容を載せた主要要素だけを再構築してください。" +
            "人物、部屋、壁、家具、風景、照明など、文字主体と無関係な背景は無視し、HTML に再現しないでください。" +
            "候補が複数ある場合は、選択範囲内で最も主要かつ完全で、文字に関係する一つだけを再構築してください。" +
            "主体要素の元画像における位置、階層、文字サイズ、配置、色の関係を再現してください。各訳文は元の文字の見た目の高さ、行高、太さ、占有比率を保ってください。" +
            "すべてを小さい同一サイズにしたり、長い訳文を収めるために全体を縮小したりせず、改行と局所的な字間・配置調整を優先してください。" +
            "2048 ピクセルの長辺を持つブラウザ表示でも同じように鮮明になるよう、vw、vh、百分率などのビューポート相対 CSS 単位を優先してください。" +
            "Markdown のコードフェンスを使わず、完全な単一 HTML 文書だけを返してください。CSS はすべて文書内に記述し、スクリプト、フォーム、外部リンク、外部画像、外部フォント、ネットワーク資源は禁止です。" +
            "ページは指定された表示領域に収まり、html と body は全幅・全高を使い、スクロールバーを出さないでください。見える文字がない場合は、完全な HTML 文書内に [NO_TEXT] を表示してください。",
        CustomCommandSystemPrompt =
            "あなたは VR ビジュアルアシスタントです。スクリーンショットと音声認識されたユーザー指示を組み合わせて回答してください。画像内で実際に見える情報を優先し、不確かな場合は明示してください。" +
            "VR オーバーレイで読みやすい自然な文章だけを出力し、JSON やコードブロックは出力しないでください。",
        CustomCommandPrompt =
            "添付された VR スクリーンショットを使ってユーザーの指示を実行してください。回答は Markdown で整形してください。HTML、JSON、コードフェンス、画像、外部リソースは出力しないでください。" +
            "回答に必要でない限り、指示を繰り返さないでください。",
        VoiceTranslationSystemPrompt =
            "あなたはリアルタイム音声翻訳アシスタントです。音声認識されたテキストを指定された対象言語へ正確に翻訳してください。" +
            "そのまま送信できる自然な訳文だけを出力し、説明、原文の繰り返し、Markdown、JSON、ラベルは出力しないでください。",
        VoiceTranslationPrompt =
            "次の音声認識テキストを翻訳してください。意味、口調、固有名詞、必要な句読点を保ち、" +
            "原文にない情報を追加しないでください。",
        SubtitleTranslationSystemPrompt =
            "あなたはリアルタイム字幕翻訳アシスタントです。入力は放送、ナレーション、動画、または複数話者の会話から得られます。" +
            "指定された対象言語へ正確に翻訳し、字幕として読みやすい簡潔で自然な訳文だけを出力してください。説明、ラベル、Markdown は不要です。",
        SubtitleTranslationPrompt =
            "次の字幕区間を翻訳してください。口調、固有名詞、会話の文脈を保ち、原文にない情報を追加しないでください。"
    };
}

public sealed class SubtitleConfiguration
{
    public bool Enabled { get; set; }

    public string AsrBackend { get; set; } = SubtitleAsrBackends.SenseVoice;

    public string VibeVoiceServiceUrl { get; set; } = "http://127.0.0.1:5090";

    public string VibeVoiceApiKey { get; set; } = string.Empty;

    public bool ShowOriginalText { get; set; } = true;

    public bool TranslateText { get; set; } = true;

    public string TargetLanguage { get; set; } = "zh-CN";

    public int MaximumHistoryEntries { get; set; } = 100;

    public int MaximumHistoryCharacters { get; set; } = 50000;

    public double WindowWidthMeters { get; set; } = 0.42;

    public double WindowDistanceMeters { get; set; } = 0.8;

    public double WindowOpacity { get; set; } = 0.92;

    public bool UseSpeakerColors { get; set; } = true;

    public SubtitleDiarizationConfiguration Diarization { get; set; } = new();

    [JsonIgnore]
    public string TranslationSystemPrompt { get; set; } =
        BuiltInPromptDefaults.SubtitleTranslationSystemPrompt;

    [JsonIgnore]
    public string TranslationPrompt { get; set; } =
        BuiltInPromptDefaults.SubtitleTranslationPrompt;
}

public static class SubtitleAsrBackends
{
    public const string SenseVoice = "sensevoice";
    public const string VibeVoiceApi = "vibevoice-api";

    public static string Normalize(string? value) =>
        string.Equals(value, VibeVoiceApi, StringComparison.OrdinalIgnoreCase)
            ? VibeVoiceApi
            : SenseVoice;
}

public sealed class SubtitleDiarizationConfiguration
{
    public bool Enabled { get; set; } = true;

    public int CpuThreadCount { get; set; } = 1;

    public double ClusteringThreshold { get; set; } = 0.9;

    public string SegmentationModelPath { get; set; } =
        "models/subtitle-diarization/pyannote-segmentation-3.0-int8.onnx";

    public string EmbeddingModelPath { get; set; } =
        "models/subtitle-diarization/3dspeaker-eres2net.onnx";
}

public sealed class VrChatVoiceInputConfiguration
{
    public const int DefaultStreamingChunkIntervalMilliseconds = 1100;
    public const int MinimumStreamingChunkIntervalMilliseconds = 0;
    public const int MaximumStreamingChunkIntervalMilliseconds = 10000;

    public bool Enabled { get; set; }

    public bool DesktopHotKeyEnabled { get; set; } = true;

    public int DesktopHotKeyVirtualKey { get; set; } = 0xA2;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9000;

    public bool SendImmediately { get; set; } = true;

    public int MaxChatboxCharacters { get; set; } = 144;

    public int StreamingChunkIntervalMilliseconds { get; set; } =
        DefaultStreamingChunkIntervalMilliseconds;

    public string DownloadSource { get; set; } = "official";

    public bool TranslationEnabled { get; set; }

    public string TranslationDisplayMode { get; set; } =
        VoiceTranslationDisplayModes.TranslationOnly;

    public string TranslationTargetLanguage { get; set; } = "zh-CN";

    [JsonIgnore]
    public string TranslationSystemPrompt { get; set; } =
        BuiltInPromptDefaults.VoiceTranslationSystemPrompt;

    [JsonIgnore]
    public string TranslationPrompt { get; set; } =
        BuiltInPromptDefaults.VoiceTranslationPrompt;
}

public static class VoiceTranslationDisplayModes
{
    public const string TranslationOnly = "translation-only";
    public const string OriginalThenTranslation = "original-then-translation";

    public static string Normalize(string? value) =>
        string.Equals(value, OriginalThenTranslation, StringComparison.OrdinalIgnoreCase)
            ? OriginalThenTranslation
            : TranslationOnly;
}
