using System.IO;
using System.Text.Json;
using SteamVRTranslator.App.SteamVR;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SteamVrManifestTests
{
    [Fact]
    public void GlobalToggleAndResultsScrollingUseRightStickWithoutSharingPriority()
    {
        var assembly = typeof(SteamVrManifestStore).Assembly;
        var actions = ReadResource(assembly, "translator_actions.json");
        Assert.Contains("/actions/global/in/toggle_visibility", actions);
        Assert.DoesNotContain("/actions/results/in/toggle_visibility", actions);
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
            var global = BindingSection(json, "/actions/global");
            var results = BindingSection(json, "/actions/results");
            Assert.Contains("/actions/global/in/toggle_visibility", global);
            Assert.Contains("/actions/results/in/scroll", results);
            Assert.DoesNotContain("toggle_visibility", results);
            Assert.Contains("/actions/results/in/left_pointer_click", results);
            Assert.Contains("/actions/results/in/right_pointer_click", results);
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
            var results = BindingSection(json, "/actions/results");
            Assert.Contains("/actions/results/in/left_grip", results);
            Assert.Contains("/actions/results/in/right_grip", results);
            Assert.Contains("/actions/results/in/left_pointer_click", results);
            Assert.Contains("/actions/results/in/right_pointer_click", results);
        }
    }

    private static string ReadResource(System.Reflection.Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream($"SteamVRTranslator.App.SteamVR.{name}");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string BindingSection(string json, string actionSet)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("bindings")
            .GetProperty(actionSet)
            .GetRawText();
    }

}
