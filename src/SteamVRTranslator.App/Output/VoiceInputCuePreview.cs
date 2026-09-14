using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Output;

/// <summary>Plays a finite cue sample to the explicitly chosen preview destination.</summary>
internal static class VoiceInputCuePreview
{
    public static async Task PlayAsync(VoiceInputCueConfiguration configuration, string deviceId, CancellationToken token,
        Func<string, ISampleProvider, IVoiceInputCuePlayback>? createPlayback = null,
        VoiceInputCueEchoOptions? echoOptions = null)
    {
        var settings = configuration.Clone();
        settings.Normalize();
        if (settings.VolumePercent == 0 || (!settings.StartEnabled && !settings.OngoingEnabled && !settings.EndEnabled))
            throw new InvalidOperationException(AppLocalization.Text("Voice.Cues.Preview.Empty"));
        var source = await Task.Run(() => new VoiceInputCueSampleProvider(settings), token).ConfigureAwait(false);
        createPlayback ??= (id, samples) => new WasapiVoiceInputCuePlayback(id, samples);
        using var playback = await Task.Run(() => createPlayback(deviceId, source), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        playback.Play();
        using var echo = echoOptions is null ? null : await Task.Run(() => VoiceInputCueEcho.TryStart(settings,
            echoOptions with { ExcludedDeviceIds = echoOptions.ExcludedDeviceIds.Append(deviceId).ToArray() }, createPlayback, source.Sounds)).ConfigureAwait(false);
        var duration = source.OpeningDuration + source.OngoingPreviewDuration;
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var delay = Task.Delay(duration, delayCancellation.Token);
            if (await Task.WhenAny(delay, playback.Completion).ConfigureAwait(false) == playback.Completion)
            {
                // Surface device failures immediately instead of silently completing a test.
                await playback.Completion.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException(AppLocalization.Text("Voice.Cues.Preview.Interrupted"));
            }
            await delay.ConfigureAwait(false);
            source.End();
            echo?.End();
            await playback.Completion.WaitAsync(source.EndingTimeout, token).ConfigureAwait(false);
            if (echo is not null) await echo.Completion.WaitAsync(source.EndingTimeout, token).ConfigureAwait(false);
        }
        finally { delayCancellation.Cancel(); }
    }
}
