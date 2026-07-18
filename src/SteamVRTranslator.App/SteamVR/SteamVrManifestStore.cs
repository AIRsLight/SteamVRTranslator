using System.Reflection;
using System.Text.Json;

namespace SteamVRTranslator.App.SteamVR;

internal static class SteamVrManifestStore
{
    public const string ApplicationKey = "io.steamvrtranslator.desktop";

    private static readonly string[] AssetNames =
    [
        "translator_actions.json",
        "bindings_generic.json",
        "bindings_vive_controller.json",
        "bindings_knuckles.json",
        "bindings_oculus_touch.json",
        "bindings_holographic_controller.json"
    ];

    public static SteamVrManifestPaths EnsureExtracted()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamVRTranslator",
            "SteamVR");
        Directory.CreateDirectory(directory);

        var assembly = typeof(SteamVrManifestStore).Assembly;
        foreach (var assetName in AssetNames)
        {
            var resourceName = $"SteamVRTranslator.App.SteamVR.{assetName}";
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"缺少内嵌 SteamVR 资源：{assetName}");
            using var destination = File.Create(Path.Combine(directory, assetName));
            source.CopyTo(destination);
        }

        var actionManifestPath = Path.Combine(directory, "translator_actions.json");
        var applicationManifestPath = Path.Combine(directory, "translator.vrmanifest");
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        var manifest = new Dictionary<string, object?>
        {
            ["source"] = "builtin",
            ["applications"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["app_key"] = ApplicationKey,
                    ["launch_type"] = "binary",
                    ["binary_path_windows"] = executablePath,
                    ["working_directory"] = Path.GetDirectoryName(executablePath),
                    ["action_manifest_path"] = actionManifestPath,
                    ["is_dashboard_overlay"] = false,
                    ["strings"] = new Dictionary<string, object?>
                    {
                        ["en_us"] = new Dictionary<string, string>
                        {
                            ["name"] = "SteamVR Translator",
                            ["description"] = "Two-controller region selection and image translation"
                        },
                        ["zh_cn"] = new Dictionary<string, string>
                        {
                            ["name"] = "SteamVR 图像翻译",
                            ["description"] = "使用双手柄框选并翻译 VR 画面"
                        }
                    }
                }
            }
        };
        File.WriteAllText(
            applicationManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return new SteamVrManifestPaths(actionManifestPath, applicationManifestPath);
    }
}

internal sealed record SteamVrManifestPaths(string ActionManifestPath, string ApplicationManifestPath);

