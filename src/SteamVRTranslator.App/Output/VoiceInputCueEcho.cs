using NAudio.CoreAudioApi;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Output;

internal sealed record VoiceInputCueEchoOptions(AppLog Log, IReadOnlyCollection<string> ExcludedDeviceIds,
    Func<string?>? GetDeviceId = null);

/// <summary>Optional local cue playback whose failures cannot interrupt the game channel.</summary>
internal sealed class VoiceInputCueEcho : IDisposable
{
    private readonly VoiceInputCueSampleProvider _source;
    private readonly IVoiceInputCuePlayback _playback;
    private readonly AppLog _log;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private VoiceInputCueEcho(VoiceInputCueSampleProvider source, IVoiceInputCuePlayback playback, AppLog log)
    {
        _source = source;
        _playback = playback;
        _log = log;
    }

    public Task Completion => _finished.Task;
    public void End() => _source.End();

    public static VoiceInputCueEcho? TryStart(VoiceInputCueConfiguration configuration, VoiceInputCueEchoOptions options,
        Func<string, ISampleProvider, IVoiceInputCuePlayback> createPlayback, VoiceInputCueSounds? sounds = null)
    {
        if (!configuration.EchoEnabled) return null;
        VoiceInputCueEcho? echo = null;
        try
        {
            var deviceId = (options.GetDeviceId ?? GetDefaultDeviceId)();
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new InvalidOperationException("Windows default playback device is unavailable.");
            if (options.ExcludedDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
            {
                options.Log.Warning("[voice-cues] 跳过本机回响：Windows 默认输出是 VB-CABLE，请将默认输出选为耳机或扬声器。");
                return null;
            }
            // Each output needs its own cursor. Sharing an ISampleProvider would split
            // samples between the game and headphones instead of duplicating the sound.
            var source = new VoiceInputCueSampleProvider(configuration, sounds);
            echo = new VoiceInputCueEcho(source, createPlayback(deviceId, source), options.Log);
            echo._playback.Play();
            options.Log.Info($"[voice-cues] 本机回响开始：输出设备 ID={deviceId}");
            _ = echo.ObserveAsync();
            return echo;
        }
        catch (Exception exception)
        {
            echo?.Dispose();
            options.Log.Error("[voice-cues] 无法启动本机回响，游戏提示音将继续。", exception);
            return null;
        }
    }

    private static string GetDefaultDeviceId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }

    private async Task ObserveAsync()
    {
        try { await _playback.Completion.ConfigureAwait(false); }
        catch (Exception exception) { _log.Error("[voice-cues] 本机回响中断，游戏提示音将继续。", exception); }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _playback.Dispose(); }
        catch (Exception exception) { _log.Error("[voice-cues] 释放本机回响失败。", exception); }
        finally { _finished.TrySetResult(); }
    }
}
