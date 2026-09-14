using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.Wave;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

internal sealed class ProcessLoopbackAudioCapture : ISubtitleAudioCapture
{
    private const string ProcessLoopbackDevice = "VAD\\Process_Loopback";
    private const uint SilentBufferFlag = 0x2;
    private const int MinimumSupportedWindowsBuild = 20348;
    private static readonly Guid AudioClientInterfaceId =
        new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientInterfaceId =
        new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    private readonly uint _processId;
    private readonly AppLog _log;
    private readonly SpeechSegmenter _segmenter = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly AutoResetEvent _sampleReady = new(false);
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Task? _captureTask;
    private readonly object _lifecycleSync = new();
    private Task? _startTask;
    private Task? _disposeTask;
    private bool _disposed;

    public ProcessLoopbackAudioCapture(uint processId, AppLog log)
    {
        if (processId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId));
        }

        _processId = processId;
        _log = log;
    }

    public event EventHandler<ProcessLoopbackAudioSegment>? SegmentReady;

    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumSupportedWindowsBuild);

    public Task Completion => _captureTask ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startTask is not null) throw new InvalidOperationException("进程音频捕获已经启动。");
            return _startTask = StartCoreAsync(cancellationToken);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                $"进程音频捕获需要 Windows 10 build {MinimumSupportedWindowsBuild} 或更高版本。");
        }
        if (_captureTask is not null)
        {
            throw new InvalidOperationException("进程音频捕获已经启动。");
        }

        using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        starting.Token.ThrowIfCancellationRequested();
        _audioClient = await ActivateAudioClientAsync(_processId, starting.Token).ConfigureAwait(false);
        starting.Token.ThrowIfCancellationRequested();
        var format = WaveFormat.CreateCustomFormat(
            WaveFormatEncoding.Pcm,
            sampleRate: 44100,
            channels: 2,
            averageBytesPerSecond: 44100 * 2 * 2,
            blockAlign: 2 * 2,
            bitsPerSample: 16);
        var formatPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            Marshal.StructureToPtr(WaveFormatEx.From(format), formatPointer, false);
            var result = _audioClient.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SourceDefaultQuality,
                0,
                0,
                formatPointer,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            Marshal.FreeCoTaskMem(formatPointer);
        }

        var captureClientPointer = IntPtr.Zero;
        try
        {
            var serviceId = AudioCaptureClientInterfaceId;
            Marshal.ThrowExceptionForHR(_audioClient.GetService(ref serviceId, out captureClientPointer));
            _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureClientPointer);
        }
        finally
        {
            if (captureClientPointer != IntPtr.Zero)
            {
                Marshal.Release(captureClientPointer);
            }
        }

        Marshal.ThrowExceptionForHR(
            _audioClient.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle()));
        Marshal.ThrowExceptionForHR(_audioClient.Start());
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancellation.Token);
        _captureTask = Task.Run(() => CaptureLoop(format, linked), CancellationToken.None);
        _log.Info($"[subtitles] 已开始捕获进程音频：PID={_processId}，格式={format}。");
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        _cancellation.Cancel();
        if (_startTask is not null)
        {
            try { await _startTask.ConfigureAwait(false); }
            catch (Exception) { /* Startup failure is reported by StartAsync. */ }
        }
        _sampleReady.Set();
        if (_captureTask is not null)
        {
            try
            {
                await _captureTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _log.Error("[subtitles] 停止进程音频捕获时发生错误。", exception);
            }
        }

        try
        {
            _audioClient?.Stop();
        }
        catch (Exception)
        {
        }

        ReleaseComObject(_captureClient);
        ReleaseComObject(_audioClient);
        _captureClient = null;
        _audioClient = null;
        _sampleReady.Dispose();
        _cancellation.Dispose();
        _log.Info($"[subtitles] 已停止捕获进程音频：PID={_processId}。");
    }

    private void CaptureLoop(WaveFormat format, CancellationTokenSource linked)
    {
        using (linked)
        {
            var waitHandles = new[] { _sampleReady, linked.Token.WaitHandle };
            var clock = Stopwatch.StartNew();
            var lastPacket = TimeSpan.Zero;
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    if (WaitHandle.WaitAny(waitHandles, 100) == 1 || linked.IsCancellationRequested)
                    {
                        break;
                    }

                    if (DrainPackets(format)) lastPacket = clock.Elapsed;
                    else if (clock.Elapsed - lastPacket >= TimeSpan.FromSeconds(0.65))
                    {
                        // Some applications stop producing packets entirely after speaking.
                        // Silence must still finish a segment without requiring Stop Listening.
                        var segment = _segmenter.Flush(format);
                        if (segment is not null) SegmentReady?.Invoke(this, segment);
                        _segmenter.SkipToFrame((long)(clock.Elapsed.TotalSeconds * format.SampleRate));
                    }
                }
            }
            finally
            {
                var final = _segmenter.Flush(format);
                if (final is not null)
                {
                    SegmentReady?.Invoke(this, final);
                }
            }
        }
    }

    private bool DrainPackets(WaveFormat format)
    {
        if (_captureClient is null)
        {
            return false;
        }

        var received = false;
        while (!_cancellation.IsCancellationRequested)
        {
            Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out var packetFrames));
            if (packetFrames == 0)
            {
                return received;
            }

            IntPtr data = IntPtr.Zero;
            uint frames = 0;
            try
            {
                Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(
                    out data,
                    out frames,
                    out var flags,
                    out _,
                    out _));
                var byteCount = checked((int)frames * format.BlockAlign);
                received = true;
                var buffer = new byte[byteCount];
                if ((flags & SilentBufferFlag) == 0 && data != IntPtr.Zero)
                {
                    Marshal.Copy(data, buffer, 0, byteCount);
                }

                var segment = _segmenter.Push(buffer, frames, format);
                if (segment is not null)
                {
                    SegmentReady?.Invoke(this, segment);
                }
            }
            finally
            {
                if (frames > 0)
                {
                    Marshal.ThrowExceptionForHR(_captureClient.ReleaseBuffer(frames));
                }
            }
        }
        return received;
    }

    private static async Task<IAudioClient> ActivateAudioClientAsync(
        uint processId,
        CancellationToken cancellationToken)
    {
        var completion = new ActivationCompletionHandler();
        var activation = new AudioClientActivationParameters
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParameters = new AudioClientProcessLoopbackParameters
            {
                TargetProcessId = processId,
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
            }
        };
        var activationPointer = Marshal.AllocCoTaskMem(
            Marshal.SizeOf<AudioClientActivationParameters>());
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.StructureToPtr(activation, activationPointer, false);
            var parameters = new PropVariant
            {
                VariantType = 65,
                Blob = new Blob
                {
                    Size = Marshal.SizeOf<AudioClientActivationParameters>(),
                    Data = activationPointer
                }
            };
            var interfaceId = AudioClientInterfaceId;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                ProcessLoopbackDevice,
                ref interfaceId,
                ref parameters,
                completion,
                out operation));
            try
            {
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                // Native activation cannot be cancelled. Retain its parameters until the
                // late callback, then release the unused client without blocking shutdown.
                _ = ReleaseAbandonedActivationAsync(completion, operation, activationPointer);
                activationPointer = IntPtr.Zero;
                operation = null;
                throw;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activationPointer);
            ReleaseComObject(operation);
            GC.KeepAlive(completion);
        }
    }

    private static async Task ReleaseAbandonedActivationAsync(ActivationCompletionHandler completion,
        IActivateAudioInterfaceAsyncOperation? operation, IntPtr parameters)
    {
        try { ReleaseComObject(await completion.Task.ConfigureAwait(false)); }
        catch (Exception) { }
        finally
        {
            Marshal.FreeCoTaskMem(parameters);
            ReleaseComObject(operation);
            GC.KeepAlive(completion);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid interfaceId,
        ref PropVariant activationParameters,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    private sealed class SpeechSegmenter
    {
        private const double VoiceThreshold = 0.0025;
        private const double PreRollSeconds = 0.20;
        private const double EndingSilenceSeconds = 0.65;
        private const double MinimumSegmentSeconds = 0.28;
        private const double MaximumSegmentSeconds = 12;
        private readonly Queue<byte[]> _preRoll = new();
        private readonly MemoryStream _active = new();
        private long _preRollFrames;
        private long _activeStartFrame;
        private long _activeFrames;
        private long _silenceFrames;
        private long _totalFrames;
        private bool _isActive;

        public ProcessLoopbackAudioSegment? Push(
            byte[] data,
            uint frameCount,
            WaveFormat format)
        {
            var voiced = HasAudibleSamples(data);
            if (!_isActive)
            {
                AddPreRoll(data, frameCount, format.SampleRate);
                if (!voiced)
                {
                    _totalFrames += frameCount;
                    return null;
                }

                _isActive = true;
                _activeStartFrame = Math.Max(0, _totalFrames - _preRollFrames + frameCount);
                foreach (var buffer in _preRoll)
                {
                    _active.Write(buffer);
                }
                _activeFrames = _preRollFrames;
                _silenceFrames = 0;
                _preRoll.Clear();
                _preRollFrames = 0;
            }
            else
            {
                _active.Write(data);
                _activeFrames += frameCount;
            }

            _totalFrames += frameCount;
            _silenceFrames = voiced ? 0 : _silenceFrames + frameCount;
            var duration = (double)_activeFrames / format.SampleRate;
            if (_silenceFrames < format.SampleRate * EndingSilenceSeconds &&
                duration < MaximumSegmentSeconds)
            {
                return null;
            }

            return Complete(format);
        }

        public ProcessLoopbackAudioSegment? Flush(WaveFormat format) =>
            _isActive ? Complete(format) : null;

        public void SkipToFrame(long frame)
        {
            _totalFrames = Math.Max(_totalFrames, frame);
            _preRoll.Clear();
            _preRollFrames = 0;
        }

        private void AddPreRoll(byte[] data, uint frames, int sampleRate)
        {
            _preRoll.Enqueue(data);
            _preRollFrames += frames;
            var maximumFrames = (long)(sampleRate * PreRollSeconds);
            while (_preRollFrames > maximumFrames && _preRoll.Count > 1)
            {
                var removed = _preRoll.Dequeue();
                _preRollFrames -= removed.Length / 4;
            }
        }

        private ProcessLoopbackAudioSegment? Complete(WaveFormat format)
        {
            var bytes = _active.ToArray();
            var start = TimeSpan.FromSeconds((double)_activeStartFrame / format.SampleRate);
            var end = TimeSpan.FromSeconds((double)(_activeStartFrame + _activeFrames) / format.SampleRate);
            var duration = end - start;
            _active.SetLength(0);
            _activeFrames = 0;
            _silenceFrames = 0;
            _isActive = false;
            return duration.TotalSeconds >= MinimumSegmentSeconds
                ? new ProcessLoopbackAudioSegment(bytes, format, start, end)
                : null;
        }

        private static bool HasAudibleSamples(byte[] data)
        {
            if (data.Length < 2)
            {
                return false;
            }

            double sum = 0;
            var samples = data.Length / 2;
            for (var index = 0; index < data.Length - 1; index += 2)
            {
                var sample = (short)(data[index] | (data[index + 1] << 8));
                var normalized = sample / 32768d;
                sum += normalized * normalized;
            }
            return Math.Sqrt(sum / samples) >= VoiceThreshold;
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCompletionHandler :
        IActivateAudioInterfaceCompletionHandler,
        IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IAudioClient> Task => _completion.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? activatedInterface = null;
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(
                    out var activationResult,
                    out activatedInterface));
                Marshal.ThrowExceptionForHR(activationResult);
                if (_completion.TrySetResult((IAudioClient)activatedInterface))
                    activatedInterface = null;
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally { ReleaseComObject(activatedInterface); }
            return 0;
        }

    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;

        public static WaveFormatEx From(WaveFormat format) => new()
        {
            FormatTag = (ushort)format.Encoding,
            Channels = (ushort)format.Channels,
            SamplesPerSecond = (uint)format.SampleRate,
            AverageBytesPerSecond = (uint)format.AverageBytesPerSecond,
            BlockAlign = (ushort)format.BlockAlign,
            BitsPerSample = (ushort)format.BitsPerSample,
            ExtraSize = 0
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParameters
    {
        public AudioClientActivationType ActivationType;
        public AudioClientProcessLoopbackParameters ProcessLoopbackParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParameters
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public Blob Blob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    private enum AudioClientActivationType
    {
        Default = 0,
        ProcessLoopback = 1
    }

    private enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1
    }

    private enum AudioClientShareMode
    {
        Shared = 0,
        Exclusive = 1
    }

    [Flags]
    private enum AudioClientStreamFlags : uint
    {
        Loopback = 0x00020000,
        EventCallback = 0x00040000,
        SourceDefaultQuality = 0x08000000,
        AutoConvertPcm = 0x80000000
    }

    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject;

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(
            out int activationResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            AudioClientShareMode shareMode,
            AudioClientStreamFlags streamFlags,
            long bufferDuration,
            long periodicity,
            IntPtr format,
            IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, out IntPtr service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr data,
            out uint frames,
            out uint flags,
            out ulong devicePosition,
            out ulong performanceCounterPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

internal sealed record ProcessLoopbackAudioSegment(
    byte[] PcmBytes,
    WaveFormat Format,
    TimeSpan Start,
    TimeSpan End);
