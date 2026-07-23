using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.AndroidMirror;

internal enum AndroidTouchAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
    Cancel = 3
}

internal enum AndroidKeyAction : byte
{
    Down = 0,
    Up = 1
}

internal enum AndroidNavigationKey
{
    Home = 3,
    Back = 4,
    RecentApps = 187
}

internal sealed record AndroidTouchEvent(
    AndroidTouchAction Action,
    int X,
    int Y,
    int ScreenWidth,
    int ScreenHeight);

internal enum AndroidVideoDecodeMode
{
    PacketOnly,
    Software,
    D3D11Hardware
}

internal sealed class ScrcpyAndroidSession : IAsyncDisposable
{
    private const string DeviceServerPath = "/data/local/tmp/scrcpy-server.jar";
    private const ulong VirtualFingerPointerId = unchecked((ulong)-3L);
    private readonly AndroidMirrorRuntimeService _runtime;
    private readonly AppLog _log;
    private readonly AndroidVideoDecodeMode _decodeMode;
    private readonly string? _decoderRuntimeDirectory;
    private readonly Channel<IReadOnlyList<byte[]>> _controlMessages =
        Channel.CreateBounded<IReadOnlyList<byte[]>>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private CancellationTokenSource? _cancellation;
    private TcpClient? _videoClient;
    private TcpClient? _controlClient;
    private Process? _serverProcess;
    private Task? _decodeTask;
    private Task? _controlTask;
    private AdbClient? _adb;
    private string? _serial;
    private int _forwardedPort;
    private long _encodedFramesReceived;
    private long _decodedFramesReceived;
    private long _framesReceived;
    private long _touchEventsQueued;
    private long _pixelConversionTicks;
    private long _hardwareTransferTicks;

    public ScrcpyAndroidSession(
        AndroidMirrorRuntimeService runtime,
        AppLog log,
        bool decodeVideo = true,
        string? decoderRuntimeDirectory = null)
        : this(
            runtime,
            log,
            decodeVideo ? AndroidVideoDecodeMode.Software : AndroidVideoDecodeMode.PacketOnly,
            decoderRuntimeDirectory)
    {
    }

    public ScrcpyAndroidSession(
        AndroidMirrorRuntimeService runtime,
        AppLog log,
        AndroidVideoDecodeMode decodeMode,
        string? decoderRuntimeDirectory = null)
    {
        _runtime = runtime;
        _log = log;
        _decodeMode = decodeMode;
        _decoderRuntimeDirectory = decoderRuntimeDirectory;
    }

    public bool IsRunning => _cancellation is { IsCancellationRequested: false };

    public long EncodedFramesReceived => Interlocked.Read(ref _encodedFramesReceived);

    public long DecodedFramesReceived => Interlocked.Read(ref _decodedFramesReceived);

    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    internal long TouchEventsQueued => Interlocked.Read(ref _touchEventsQueued);

    public TimeSpan PixelConversionTime =>
        TimeSpan.FromTicks(Interlocked.Read(ref _pixelConversionTicks));

    public TimeSpan HardwareTransferTime =>
        TimeSpan.FromTicks(Interlocked.Read(ref _hardwareTransferTicks));

    public event EventHandler<AndroidVideoFrame>? FrameReceived;

    public event EventHandler<string>? StatusChanged;

    public async Task StartAsync(
        AndroidDeviceInfo device,
        AndroidMirrorConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException(AppLocalization.Text("AndroidMirror.Error.AlreadyRunning"));
        }
        if (!_runtime.IsInstalled)
        {
            throw new InvalidOperationException(AppLocalization.Text("AndroidMirror.Error.RuntimeMissing"));
        }
        if (!device.IsOnline)
        {
            throw new InvalidOperationException(AppLocalization.Format(
                "AndroidMirror.Error.DeviceUnavailable",
                device.Serial,
                device.State));
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _encodedFramesReceived, 0);
        Interlocked.Exchange(ref _decodedFramesReceived, 0);
        Interlocked.Exchange(ref _framesReceived, 0);
        Interlocked.Exchange(ref _touchEventsQueued, 0);
        Interlocked.Exchange(ref _pixelConversionTicks, 0);
        Interlocked.Exchange(ref _hardwareTransferTicks, 0);
        var token = _cancellation.Token;
        _serial = device.Serial;
        _adb = new AdbClient(_runtime.AdbPath);
        var scid = Random.Shared.Next(1, int.MaxValue);
        var socketName = $"scrcpy_{scid:x8}";
        try
        {
            Publish(AppLocalization.Format("AndroidMirror.Status.Connecting", device.DisplayName));
            var push = await _adb.RunForDeviceAsync(
                device.Serial,
                ["push", _runtime.ServerPath, DeviceServerPath],
                token);
            push.EnsureSuccess("部署 scrcpy server");

            var forward = await _adb.RunForDeviceAsync(
                device.Serial,
                ["forward", "tcp:0", $"localabstract:{socketName}"],
                token);
            forward.EnsureSuccess("创建 ADB 端口转发");
            if (!int.TryParse(forward.StandardOutput.Trim(), out _forwardedPort))
            {
                throw new InvalidOperationException(
                    $"ADB 未返回有效的转发端口：{forward.StandardOutput.Trim()}");
            }

            var maxSize = Math.Clamp(
                AndroidMirrorConfiguration.NormalizeMaximumSize(configuration.MaximumSize),
                AndroidMirrorConfiguration.MinimumSize,
                AndroidMirrorConfiguration.MaximumAllowedSize);
            var maxFps = Math.Clamp(configuration.MaximumFramesPerSecond, 10, 120);
            var bitRate = Math.Clamp(configuration.VideoBitRateMbps, 1, 20) * 1_000_000;
            _serverProcess = _adb.StartForDevice(
                device.Serial,
                [
                    "shell",
                    $"CLASSPATH={DeviceServerPath}",
                    "app_process",
                    "/",
                    "com.genymobile.scrcpy.Server",
                    AndroidMirrorRuntimeService.ScrcpyVersion,
                    $"scid={scid:x8}",
                    "log_level=info",
                    "audio=false",
                    "video_codec=h264",
                    $"video_bit_rate={bitRate}",
                    $"max_size={maxSize}",
                    $"max_fps={maxFps}",
                    "tunnel_forward=true",
                    "send_device_meta=false",
                    "send_stream_meta=false",
                    "send_frame_meta=true",
                    "send_dummy_byte=false"
                ],
                line => _log.Info($"[android-mirror/server] {line}"));

            await WaitForServerSocketAsync(_adb, device.Serial, socketName, token);
            _videoClient = await ConnectWithRetryAsync(_forwardedPort, token);
            _controlClient = await ConnectWithRetryAsync(_forwardedPort, token);
            _decodeTask = DecodeLoopAsync(_videoClient.GetStream(), token);
            _controlTask = ControlLoopAsync(_controlClient.GetStream(), token);
            Publish(AppLocalization.Format("AndroidMirror.Status.Waiting", device.DisplayName));
            _log.Info(
                $"[android-mirror] 会话已启动：设备={device.Serial}，端口={_forwardedPort}，" +
                $"上限={maxSize}px/{maxFps}fps/{configuration.VideoBitRateMbps}Mbps。");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public bool QueueTouch(AndroidTouchEvent touchEvent)
    {
        var queued = IsRunning && _controlMessages.Writer.TryWrite([BuildTouchMessage(touchEvent)]);
        if (queued)
        {
            Interlocked.Increment(ref _touchEventsQueued);
        }
        return queued;
    }

    public bool QueueNavigationKey(AndroidNavigationKey key) =>
        IsRunning && _controlMessages.Writer.TryWrite(
        [
            BuildKeyCodeMessage(AndroidKeyAction.Down, key),
            BuildKeyCodeMessage(AndroidKeyAction.Up, key)
        ]);

    public async Task StopAsync()
    {
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        _videoClient?.Dispose();
        _controlClient?.Dispose();
        await IgnoreCancellationAsync(_decodeTask);
        await IgnoreCancellationAsync(_controlTask);
        _decodeTask = null;
        _controlTask = null;
        _videoClient = null;
        _controlClient = null;
        if (_serverProcess is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch
            {
            }
        }
        _serverProcess?.Dispose();
        _serverProcess = null;
        if (_adb is not null && _serial is not null && _forwardedPort > 0)
        {
            try
            {
                await _adb.RunForDeviceAsync(
                    _serial,
                    ["forward", "--remove", $"tcp:{_forwardedPort}"],
                    CancellationToken.None);
            }
            catch
            {
            }
        }

        _forwardedPort = 0;
        _serial = null;
        _adb = null;
        cancellation.Dispose();
        Publish(AppLocalization.Text("AndroidMirror.Status.Stopped"));
        _log.Info("[android-mirror] 会话已停止。");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task DecodeLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() =>
            {
                if (_decodeMode == AndroidVideoDecodeMode.PacketOnly)
                {
                    DrainEncodedPackets(
                        stream,
                        () => Interlocked.Increment(ref _encodedFramesReceived),
                        cancellationToken);
                    return;
                }

                using var decoder = new H264StreamDecoder(
                    _decoderRuntimeDirectory ?? _runtime.RuntimeDirectory,
                    _decodeMode == AndroidVideoDecodeMode.D3D11Hardware);
                decoder.Decode(
                    stream,
                    frame =>
                    {
                        var count = Interlocked.Increment(ref _framesReceived);
                        FrameReceived?.Invoke(this, frame);
                        if (count == 1)
                        {
                            Publish(AppLocalization.Format(
                                "AndroidMirror.Status.FrameConnected",
                                frame.Width,
                                frame.Height));
                            _log.Info($"[android-mirror] 收到首帧：{frame.Width}x{frame.Height}。");
                        }
                    },
                    cancellationToken,
                    () => Interlocked.Increment(ref _encodedFramesReceived),
                    () => Interlocked.Increment(ref _decodedFramesReceived),
                    elapsed => Interlocked.Add(ref _pixelConversionTicks, elapsed.Ticks),
                    elapsed => Interlocked.Add(ref _hardwareTransferTicks, elapsed.Ticks));
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Error("[android-mirror] 视频解码失败。", exception);
            Publish(AppLocalization.Format("AndroidMirror.Status.DecodeFailed", exception.Message));
        }
    }

    private static void DrainEncodedPackets(
        Stream stream,
        Action mediaPacketHandler,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[12];
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ReadExactly(stream, header, cancellationToken))
            {
                return;
            }

            var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
            var packetSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]));
            if (packetSize is <= 0 or > 16 * 1024 * 1024)
            {
                throw new InvalidDataException($"scrcpy 返回了无效的 H.264 数据包长度：{packetSize}。");
            }

            var remaining = packetSize;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, buffer.Length);
                if (!ReadExactly(stream, buffer.AsSpan(0, chunk), cancellationToken))
                {
                    throw new EndOfStreamException("scrcpy H.264 数据包在传输完成前结束。");
                }
                remaining -= chunk;
            }

            if ((ptsAndFlags & (1UL << 62)) == 0)
            {
                mediaPacketHandler();
            }
        }
    }

    private static bool ReadExactly(
        Stream stream,
        Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                return offset == 0
                    ? false
                    : throw new EndOfStreamException("scrcpy 数据流在消息中途结束。");
            }
            offset += read;
        }
        return true;
    }

    private async Task ControlLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var messages in _controlMessages.Reader.ReadAllAsync(cancellationToken))
            {
                foreach (var message in messages)
                {
                    await stream.WriteAsync(message, cancellationToken);
                }
                await stream.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Error("[android-mirror] 触摸控制通道失败。", exception);
            Publish(AppLocalization.Format("AndroidMirror.Status.ControlFailed", exception.Message));
        }
    }

    internal static byte[] BuildTouchMessage(AndroidTouchEvent touchEvent)
    {
        var message = new byte[32];
        message[0] = 2;
        message[1] = (byte)touchEvent.Action;
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2, 8), VirtualFingerPointerId);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10, 4), touchEvent.X);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(14, 4), touchEvent.Y);
        BinaryPrimitives.WriteUInt16BigEndian(
            message.AsSpan(18, 2),
            checked((ushort)Math.Clamp(touchEvent.ScreenWidth, 1, ushort.MaxValue)));
        BinaryPrimitives.WriteUInt16BigEndian(
            message.AsSpan(20, 2),
            checked((ushort)Math.Clamp(touchEvent.ScreenHeight, 1, ushort.MaxValue)));
        BinaryPrimitives.WriteUInt16BigEndian(
            message.AsSpan(22, 2),
            touchEvent.Action is AndroidTouchAction.Up or AndroidTouchAction.Cancel
                ? (ushort)0
                : ushort.MaxValue);
        return message;
    }

    internal static byte[] BuildKeyCodeMessage(
        AndroidKeyAction action,
        AndroidNavigationKey key)
    {
        var message = new byte[14];
        message[0] = 0;
        message[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(2, 4), (int)key);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(6, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10, 4), 0);
        return message;
    }

    private static async Task<TcpClient> ConnectWithRetryAsync(
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return client;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastError = exception;
                client.Dispose();
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"无法连接 scrcpy 本地端口 {port}。",
            lastError);
    }

    private static async Task WaitForServerSocketAsync(
        AdbClient adb,
        string serial,
        string socketName,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sockets = await adb.RunForDeviceAsync(
                serial,
                ["shell", "cat", "/proc/net/unix"],
                cancellationToken);
            if (sockets.ExitCode == 0 &&
                sockets.StandardOutput.Contains(socketName, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new InvalidOperationException(
            $"scrcpy server 未在设备上创建控制套接字 {socketName}。");
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void Publish(string message) => StatusChanged?.Invoke(this, message);
}
