using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SteamVRTranslator.App.Output;

internal sealed class WasapiVoiceInputCuePlayback : IVoiceInputCuePlayback
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public WasapiVoiceInputCuePlayback(string deviceId, ISampleProvider source)
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDevice(deviceId);
        try
        {
            if (_device.DataFlow != DataFlow.Render || _device.State != DeviceState.Active)
                throw new InvalidOperationException("The selected playback endpoint is unavailable.");
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 40);
            try
            {
                // WasapiOut captures SynchronizationContext for PlaybackStopped. Construct
                // this object off the UI thread so cancellation cannot deadlock that thread.
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(source.ToWaveProvider());
            }
            catch { _output.Dispose(); throw; }
        }
        catch { _device.Dispose(); throw; }
    }

    public Task Completion => _completion.Task;
    public void Play() => _output.Play();
    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is null) _completion.TrySetResult();
        else _completion.TrySetException(args.Exception);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _output.Stop(); }
        finally
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Dispose(); }
            finally
            {
                try { _device.Dispose(); }
                finally { _completion.TrySetResult(); }
            }
        }
    }
}
