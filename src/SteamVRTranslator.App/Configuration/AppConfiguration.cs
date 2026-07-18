using System.Text.Json.Serialization;

namespace SteamVRTranslator.App.Configuration;

public sealed class AppConfiguration
{
    public int HotKeyVirtualKey { get; set; } = 0x77;

    public int SelectionTimeoutSeconds { get; set; } = 30;

    public string CaptureDirectory { get; set; } = ".";

    public string StereoCompositionMode { get; set; } = "left-eye";

    public bool InvertResultScroll { get; set; }

    public TranslationConfiguration Translation { get; set; } = new();

    public SpeechConfiguration Speech { get; set; } = new();

    public VrChatVoiceInputConfiguration VrChatVoiceInput { get; set; } = new();
}

public sealed class TranslationConfiguration
{
    public string ActiveProviderId { get; set; } = TranslationProviderConfiguration.MockProviderId;

    public List<TranslationProviderConfiguration> Providers { get; set; } =
    [
        TranslationProviderConfiguration.CreateMock(),
        new()
    ];

    public string TargetLanguage { get; set; } = "zh-CN";

    public bool EnableStreaming { get; set; } = true;

    public bool DisableThinking { get; set; } = true;

    public string SystemPrompt { get; set; } =
        "你是 VR 画面翻译助手。识别截图中的可见文字并翻译为指定目标语言。" +
        "只输出自然、可直接阅读的译文，保留必要的换行和上下文。不要解释翻译过程，也不要输出 JSON。";

    public TranslationProviderConfiguration? GetActiveProvider() =>
        Providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, ActiveProviderId, StringComparison.OrdinalIgnoreCase));
}

public sealed class TranslationProviderConfiguration
{
    public const string MockProviderId = "mock";
    public const string MockType = "mock";
    public const string OpenAiCompatibleType = "openai-compatible";

    public string Id { get; set; } = "openai";

    public string Type { get; set; } = OpenAiCompatibleType;

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsMock => string.Equals(Type, MockType, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (IsMock)
            {
                return "模拟提供商";
            }

            if (Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri))
            {
                return string.IsNullOrWhiteSpace(Model) ? uri.Host : $"{uri.Host} · {Model}";
            }

            return "未配置 Provider";
        }
    }

    public static TranslationProviderConfiguration CreateMock() => new()
    {
        Id = MockProviderId,
        Type = MockType,
        BaseUrl = string.Empty,
        ApiKey = string.Empty,
        Model = string.Empty
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

    public string SenseVoiceModelPath { get; set; } = "models/sensevoice-small-q8.gguf";

    public string? SenseVoiceVadModelPath { get; set; } = "models/fsmn-vad.gguf";

    public string CustomCommandSystemPrompt { get; set; } =
        "你是 VR 视觉助手。结合截图和用户语音转写指令回答。优先依据截图中实际可见的信息；" +
        "不确定时明确说明。直接输出适合 VR 叠加层阅读的自然文本，不要输出 JSON 或代码块。";
}

public sealed class VrChatVoiceInputConfiguration
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9000;

    public bool SendImmediately { get; set; } = true;

    public int MaxChatboxCharacters { get; set; } = 144;

    public string DownloadSource { get; set; } = "official";
}
