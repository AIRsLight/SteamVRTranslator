using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Speech;

public sealed class WasapiCommandRecorder : IDisposable
{
    private readonly SpeechConfiguration _configuration;
    private readonly AppLog _log;
    private readonly object _sync = new();
    private WasapiCapture? _capture;
    private WaveFileWriter? _writer;
    private TaskCompletionSource? _stopped;
    private Stopwatch? _duration;
    private string? _outputPath;
    private string? _deviceName;
    private long _bytesRecorded;

    public WasapiCommandRecorder(SpeechConfiguration configuration, AppLog log)
    {
        _configuration = configuration;
        _log = log;
    }

    public bool IsRecording { get; private set; }

    public static IReadOnlyList<CommandMicrophoneInfo> ListCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(device => new CommandMicrophoneInfo(device.ID, device.FriendlyName))
            .ToArray();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (IsRecording)
            {
                throw new InvalidOperationException("语音命令录音已经开始。");
            }

            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrWhiteSpace(_configuration.DeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                : enumerator.GetDevice(_configuration.DeviceId);
            _deviceName = device.FriendlyName;
            _capture = new WasapiCapture(device);
            _outputPath = ApplicationDataPaths.CreateTemporaryFilePath(
                "steamvr-translator-command",
                ".wav");
            _writer = new WaveFileWriter(_outputPath, _capture.WaveFormat);
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _duration = Stopwatch.StartNew();
            _bytesRecorded = 0;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            IsRecording = true;
            _log.Info(
                $"[audio] 录音开始：设备={_deviceName}，" +
                $"格式={_capture.WaveFormat}，临时文件={Path.GetFileName(_outputPath)}");
        }
    }

    public async Task<CommandAudioInput> StopAsync(CancellationToken cancellationToken)
    {
        Task stoppedTask;
        string outputPath;
        Stopwatch duration;
        lock (_sync)
        {
            if (!IsRecording || _capture is null || _stopped is null || _outputPath is null || _duration is null)
            {
                throw new InvalidOperationException("语音命令录音尚未开始。");
            }

            IsRecording = false;
            stoppedTask = _stopped.Task;
            outputPath = _outputPath;
            duration = _duration;
            _capture.StopRecording();
        }

        try
        {
            await stoppedTask.WaitAsync(cancellationToken);
        }
        finally
        {
            duration.Stop();
            CleanupCapture();
        }

        _log.Info(
            $"[audio] 录音停止：设备={_deviceName ?? "<未知>"}，" +
            $"持续={duration.Elapsed.TotalMilliseconds:F0} ms，PCM字节={_bytesRecorded:N0}，" +
            $"文件={Path.GetFileName(outputPath)}");

        if (duration.ElapsedMilliseconds < _configuration.MinimumDurationMilliseconds)
        {
            File.Delete(outputPath);
            throw new InvalidOperationException(
                $"语音命令录音短于 {_configuration.MinimumDurationMilliseconds} ms。");
        }

        using (var reader = new WaveFileReader(outputPath))
        {
            if (reader.Length == 0)
            {
                File.Delete(outputPath);
                throw new InvalidOperationException("麦克风没有捕获到音频数据。");
            }
        }

        return new CommandAudioInput(outputPath, duration.Elapsed);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        string? outputPath;
        Task? stoppedTask = null;
        lock (_sync)
        {
            outputPath = _outputPath;
            if (IsRecording && _capture is not null && _stopped is not null)
            {
                IsRecording = false;
                stoppedTask = _stopped.Task;
                _capture.StopRecording();
            }
        }

        try
        {
            if (stoppedTask is not null)
            {
                await stoppedTask.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            CleanupCapture();
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                File.Delete(outputPath);
                _log.Info(
                    $"[audio] 已取消并删除暂存录音：{Path.GetFileName(outputPath)}，" +
                    $"PCM字节={_bytesRecorded:N0}");
            }
        }
    }

    public void Dispose()
    {
        if (IsRecording)
        {
            CancelAsync().GetAwaiter().GetResult();
        }
        else
        {
            CleanupCapture();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        lock (_sync)
        {
            _writer?.Write(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
            _writer?.Flush();
            _bytesRecorded += eventArgs.BytesRecorded;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null)
        {
            _stopped?.TrySetResult();
        }
        else
        {
            _stopped?.TrySetException(eventArgs.Exception);
        }
    }

    private void CleanupCapture()
    {
        lock (_sync)
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
            }

            _writer?.Dispose();
            _capture = null;
            _writer = null;
            _stopped = null;
            _duration = null;
            _outputPath = null;
        }
    }
}

public sealed record CommandMicrophoneInfo(string Id, string Name);

public sealed class CommandAudioInput : IDisposable
{
    public CommandAudioInput(string filePath, TimeSpan duration)
    {
        FilePath = filePath;
        Duration = duration;
    }

    public string FilePath { get; }

    public TimeSpan Duration { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (IOException)
        {
        }
    }
}
