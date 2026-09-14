using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Output;

/// <summary>Routes PTT cues to the automatically detected base VB-CABLE playback endpoint.</summary>
internal sealed class VoiceInputCueOutput : IDisposable
{
    private readonly object _sync = new();
    private readonly VoiceInputCueConfiguration _configuration;
    private readonly VoiceInputCueSounds _sounds;
    private readonly AppLog _log;
    private readonly Func<string, ISampleProvider, IVoiceInputCuePlayback> _createPlayback;
    private readonly Func<VbCableStatus> _probe;
    private readonly Func<string?>? _getEchoDeviceId;
    private Session? _session;
    private bool _disposed;

    public VoiceInputCueOutput(
        VoiceInputCueConfiguration configuration,
        AppLog log,
        Func<string, ISampleProvider, IVoiceInputCuePlayback>? createPlayback = null,
        Func<VbCableStatus>? probe = null,
        Func<string?>? getEchoDeviceId = null)
    {
        _configuration = configuration.Clone();
        _configuration.Normalize();
        _log = log;
        _sounds = _configuration.Enabled ? VoiceInputCueSounds.Load(_configuration, fallbackLog: log) : new();
        _createPlayback = createPlayback ?? ((id, source) =>
            Task.Run(() => new WasapiVoiceInputCuePlayback(id, source)).GetAwaiter().GetResult());
        _probe = probe ?? VbCableDevice.Probe;
        _getEchoDeviceId = getEchoDeviceId;
    }

    public void Disable()
    {
        lock (_sync)
        {
            _configuration.Enabled = false;
            StopCore();
        }
    }

    public bool Start()
    {
        lock (_sync)
        {
            if (_disposed || !_configuration.Enabled ||
                (!_configuration.StartEnabled && !_configuration.OngoingEnabled && !_configuration.EndEnabled) ||
                _configuration.VolumePercent == 0)
            {
                return false;
            }

            StopCore();
            try
            {
                var status = _probe();
                if (!status.Ready)
                {
                    _log.Warning("[voice-cues] VB-CABLE 尚未安装或不可用，跳过播放。" + Environment.NewLine + status.DiagnosticDetails);
                    return false;
                }
                var source = new VoiceInputCueSampleProvider(_configuration, _sounds);
                var playback = _createPlayback(status.PlaybackDeviceId!, source);
                var session = new Session(source, playback);
                _session = session;
                playback.Play();
                session.Echo = VoiceInputCueEcho.TryStart(_configuration,
                    new VoiceInputCueEchoOptions(_log,
                        status.DeviceIds.Append(status.PlaybackDeviceId!).ToArray(), _getEchoDeviceId), _createPlayback, _sounds);
                _ = ObservePlaybackAsync(session);
                return true;
            }
            catch (Exception exception)
            {
                StopCore();
                _log.Error("[voice-cues] 提示音输出启动失败，语音识别将继续。", exception);
                return false;
            }
        }
    }

    public Task EndAsync()
    {
        lock (_sync)
        {
            _session?.Source.End();
            _session?.Echo?.End();
            return _session?.Finished.Task ?? Task.CompletedTask;
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopCore();
        }
    }

    private async Task ObservePlaybackAsync(Session session)
    {
        try
        {
            await session.Playback.Completion.ConfigureAwait(false);
            if (session.Echo is { } echo)
            {
                echo.End();
                await echo.Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _log.Error("[voice-cues] 提示音播放中断，语音识别将继续。", exception);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, session))
                {
                    StopCore();
                }
                session.Finished.TrySetResult();
            }
        }
    }

    private void StopCore()
    {
        var session = _session;
        _session = null;
        if (session is null)
        {
            return;
        }

        try
        {
            session.Playback.Dispose();
        }
        catch (Exception exception)
        {
            _log.Error("[voice-cues] 释放提示音输出失败。", exception);
        }
        finally
        {
            session.Echo?.Dispose();
            session.Finished.TrySetResult();
        }
    }

    private sealed record Session(VoiceInputCueSampleProvider Source, IVoiceInputCuePlayback Playback)
    {
        public VoiceInputCueEcho? Echo { get; set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}

internal interface IVoiceInputCuePlayback : IDisposable
{
    Task Completion { get; }
    void Play();
}
