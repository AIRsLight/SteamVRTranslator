using System.Globalization;
using System.Windows.Markup;

namespace SteamVRTranslator.VibeVoice.Manager;

public static class ManagerLocalization
{
    private static readonly Dictionary<string, string> English =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App.Title"] = "VibeVoice ASR Service",
            ["App.Subtitle"] = "Local and remote multi-speaker speech recognition",
            ["Status.Connecting"] = "Connecting",
            ["Status.Online"] = "Service online",
            ["Status.Offline"] = "Service offline",
            ["Status.Running"] = "Inference running",
            ["Status.Stopped"] = "Inference stopped",
            ["Status.Installing"] = "Installing",
            ["Status.NotInstalled"] = "Not installed",
            ["Status.Installed"] = "Installed",
            ["Status.Error"] = "Error",
            ["Sidebar.Endpoint"] = "SERVICE ADDRESS",
            ["Sidebar.OpenWeb"] = "Open web manager",
            ["Sidebar.OpenData"] = "Open data folder",
            ["Sidebar.Refresh"] = "Refresh",
            ["Settings.Title"] = "Inference settings",
            ["Settings.Description"] = "Settings apply to the resident CrispASR process.",
            ["Settings.Backend"] = "Backend",
            ["Settings.Device"] = "GPU device index",
            ["Settings.Threads"] = "CPU threads",
            ["Settings.Source"] = "Download source",
            ["Settings.Official"] = "Hugging Face",
            ["Settings.Mirror"] = "HF Mirror",
            ["Settings.Apply"] = "Apply settings",
            ["Runtime.Title"] = "Inference runtime",
            ["Runtime.Description"] = "CrispASR runtime for the selected backend.",
            ["Runtime.Download"] = "Download runtime",
            ["Runtime.Ready"] = "Selected runtime is ready",
            ["Runtime.Missing"] = "Selected runtime is not installed",
            ["Model.Title"] = "VibeVoice Q4_K model",
            ["Model.Description"] = "About 4.5 GB. Shared by CPU, Vulkan and CUDA runtimes.",
            ["Model.Download"] = "Download model",
            ["Model.Ready"] = "Q4_K model is ready",
            ["Model.Missing"] = "Q4_K model is not installed",
            ["Actions.InstallAll"] = "Install all",
            ["Actions.Start"] = "Start inference",
            ["Actions.Stop"] = "Stop inference",
            ["Actions.Cancel"] = "Cancel download",
            ["Progress.Preparing"] = "Preparing download...",
            ["Progress.Verifying"] = "Verifying downloaded file...",
            ["Error.ServiceMissing"] = "The bundled VibeVoice server executable is missing.",
            ["Error.Title"] = "Operation failed"
        };

    private static readonly Dictionary<string, string> Chinese =
        new Dictionary<string, string>(English, StringComparer.Ordinal)
        {
            ["App.Title"] = "VibeVoice ASR 服务",
            ["App.Subtitle"] = "可供本机或远程使用的多说话人语音识别服务",
            ["Status.Connecting"] = "正在连接",
            ["Status.Online"] = "服务在线",
            ["Status.Offline"] = "服务离线",
            ["Status.Running"] = "推理运行中",
            ["Status.Stopped"] = "推理已停止",
            ["Status.Installing"] = "正在安装",
            ["Status.NotInstalled"] = "未安装",
            ["Status.Installed"] = "已安装",
            ["Status.Error"] = "错误",
            ["Sidebar.Endpoint"] = "服务地址",
            ["Sidebar.OpenWeb"] = "打开网页管理器",
            ["Sidebar.OpenData"] = "打开数据目录",
            ["Sidebar.Refresh"] = "刷新状态",
            ["Settings.Title"] = "推理设置",
            ["Settings.Description"] = "设置会应用到常驻 CrispASR 推理进程。",
            ["Settings.Backend"] = "推理后端",
            ["Settings.Device"] = "GPU 设备序号",
            ["Settings.Threads"] = "CPU 线程数",
            ["Settings.Source"] = "下载来源",
            ["Settings.Official"] = "Hugging Face 官方",
            ["Settings.Mirror"] = "HF 镜像",
            ["Settings.Apply"] = "应用设置",
            ["Runtime.Title"] = "推理运行时",
            ["Runtime.Description"] = "下载与当前后端匹配的 CrispASR 运行时。",
            ["Runtime.Download"] = "下载运行时",
            ["Runtime.Ready"] = "当前后端运行时已就绪",
            ["Runtime.Missing"] = "当前后端运行时未安装",
            ["Model.Title"] = "VibeVoice Q4_K 模型",
            ["Model.Description"] = "约 4.5 GB，由 CPU、Vulkan 与 CUDA 运行时共用。",
            ["Model.Download"] = "下载模型",
            ["Model.Ready"] = "Q4_K 模型已就绪",
            ["Model.Missing"] = "Q4_K 模型未安装",
            ["Actions.InstallAll"] = "全部安装",
            ["Actions.Start"] = "启动推理",
            ["Actions.Stop"] = "停止推理",
            ["Actions.Cancel"] = "取消下载",
            ["Progress.Preparing"] = "正在准备下载...",
            ["Progress.Verifying"] = "正在校验下载文件...",
            ["Error.ServiceMissing"] = "发布包中缺少 VibeVoice 服务端程序。",
            ["Error.Title"] = "操作失败"
        };

    private static readonly Dictionary<string, string> Japanese =
        new Dictionary<string, string>(English, StringComparer.Ordinal)
        {
            ["App.Title"] = "VibeVoice ASR サービス",
            ["App.Subtitle"] = "ローカルまたはリモートで使用できる話者分離音声認識",
            ["Status.Connecting"] = "接続中",
            ["Status.Online"] = "サービスはオンラインです",
            ["Status.Offline"] = "サービスはオフラインです",
            ["Status.Running"] = "推論実行中",
            ["Status.Stopped"] = "推論停止中",
            ["Status.Installing"] = "インストール中",
            ["Status.NotInstalled"] = "未インストール",
            ["Status.Installed"] = "インストール済み",
            ["Status.Error"] = "エラー",
            ["Sidebar.Endpoint"] = "サービス URL",
            ["Sidebar.OpenWeb"] = "Web 管理画面を開く",
            ["Sidebar.OpenData"] = "データフォルダーを開く",
            ["Sidebar.Refresh"] = "更新",
            ["Settings.Title"] = "推論設定",
            ["Settings.Description"] = "常駐 CrispASR プロセスに適用されます。",
            ["Settings.Backend"] = "バックエンド",
            ["Settings.Device"] = "GPU デバイス番号",
            ["Settings.Threads"] = "CPU スレッド数",
            ["Settings.Source"] = "ダウンロード元",
            ["Settings.Official"] = "Hugging Face",
            ["Settings.Mirror"] = "HF ミラー",
            ["Settings.Apply"] = "設定を適用",
            ["Runtime.Title"] = "推論ランタイム",
            ["Runtime.Description"] = "選択したバックエンド用の CrispASR ランタイム。",
            ["Runtime.Download"] = "ランタイムをダウンロード",
            ["Runtime.Ready"] = "選択したランタイムは使用可能です",
            ["Runtime.Missing"] = "選択したランタイムは未インストールです",
            ["Model.Title"] = "VibeVoice Q4_K モデル",
            ["Model.Description"] = "約 4.5 GB。CPU、Vulkan、CUDA で共有します。",
            ["Model.Download"] = "モデルをダウンロード",
            ["Model.Ready"] = "Q4_K モデルは使用可能です",
            ["Model.Missing"] = "Q4_K モデルは未インストールです",
            ["Actions.InstallAll"] = "すべてインストール",
            ["Actions.Start"] = "推論を開始",
            ["Actions.Stop"] = "推論を停止",
            ["Actions.Cancel"] = "ダウンロードを中止",
            ["Progress.Preparing"] = "ダウンロードを準備しています...",
            ["Progress.Verifying"] = "ダウンロードを検証しています...",
            ["Error.ServiceMissing"] = "VibeVoice サーバープログラムが見つかりません。",
            ["Error.Title"] = "操作に失敗しました"
        };

    private static IReadOnlyDictionary<string, string> Strings =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? Chinese
            : CultureInfo.CurrentUICulture.Name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? Japanese
                : English;

    public static string Text(string key) =>
        Strings.TryGetValue(key, out var value)
            ? value
            : English.TryGetValue(key, out value)
                ? value
                : key;
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        ManagerLocalization.Text(Key);
}
