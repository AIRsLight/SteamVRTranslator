using System.IO;
using System.Text.Json;
using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SteamVrManifestTests
{
    [Fact]
    public void ResultsUseRightStickForScrollingAndVisibilityToggle()
    {
        var assembly = typeof(SteamVrManifestStore).Assembly;
        var actions = ReadResource(assembly, "translator_actions.json");
        Assert.Contains("/actions/results/in/toggle_visibility", actions);
        Assert.Contains("/actions/results/in/left_pointer_click", actions);
        Assert.Contains("/actions/results/in/right_pointer_click", actions);

        foreach (var binding in new[]
                 {
                     "bindings_generic.json",
                     "bindings_vive_controller.json",
                     "bindings_knuckles.json",
                     "bindings_oculus_touch.json",
                     "bindings_holographic_controller.json"
                 })
        {
            var json = ReadResource(assembly, binding);
            using var _ = JsonDocument.Parse(json);
            Assert.Contains("/actions/results/in/scroll", json);
            Assert.Contains("/actions/results/in/toggle_visibility", json);
            Assert.Contains("/actions/results/in/left_pointer_click", ResultsSection(json));
            Assert.Contains("/actions/results/in/right_pointer_click", ResultsSection(json));
        }
    }

    [Fact]
    public void VoiceInputUsesDedicatedSteamVrActionSet()
    {
        var assembly = typeof(SteamVrManifestStore).Assembly;
        var actions = ReadResource(assembly, "translator_actions.json");
        Assert.Contains("/actions/voiceinput/in/ptt", actions);

        var touch = ReadResource(assembly, "bindings_oculus_touch.json");
        Assert.Contains("/user/hand/left/input/x", touch);
        Assert.Contains("/actions/voiceinput/in/ptt", touch);

        var knuckles = ReadResource(assembly, "bindings_knuckles.json");
        Assert.Contains("/user/hand/right/input/a", knuckles);
        Assert.Contains("/actions/voiceinput/in/ptt", knuckles);
    }

    [Fact]
    public void SpatialOverlaysUseGripActionsAndRightStickClose()
    {
        var assembly = typeof(SteamVrManifestStore).Assembly;
        var actions = ReadResource(assembly, "translator_actions.json");
        Assert.Contains("/actions/results/in/left_grip", actions);
        Assert.Contains("/actions/results/in/right_grip", actions);
        Assert.Contains("/actions/results/in/left_pointer_click", actions);
        Assert.Contains("/actions/results/in/right_pointer_click", actions);

        foreach (var binding in new[]
                 {
                     "bindings_generic.json",
                     "bindings_vive_controller.json",
                     "bindings_knuckles.json",
                     "bindings_oculus_touch.json",
                     "bindings_holographic_controller.json"
                 })
        {
            var json = ReadResource(assembly, binding);
            using var _ = JsonDocument.Parse(json);
            Assert.Contains("/actions/results/in/left_grip", json);
            Assert.Contains("/actions/results/in/right_grip", json);
            Assert.Contains("/actions/results/in/toggle_visibility", json);
            Assert.Contains("/actions/results/in/left_pointer_click", json);
            Assert.Contains("/actions/results/in/right_pointer_click", json);
        }
    }

    private static string ReadResource(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream($"SteamVRTranslator.App.SteamVR.{name}");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string ResultsSection(string json)
    {
        var marker = "\"/actions/results\"";
        var index = json.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? string.Empty : json[index..];
    }

}
