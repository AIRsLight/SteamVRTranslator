using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace SteamVRTranslator.App.AndroidMirror;

internal sealed record AndroidVideoFrame(
    byte[] BgraPixels,
    int Width,
    int Height,
    long Sequence);

internal sealed unsafe class H264StreamDecoder : IDisposable
{
    private static readonly object InitializationSync = new();
    private static readonly object HardwareFormatSync = new();
    private static readonly AVCodecContext_get_format HardwareFormatSelector = SelectHardwareFormat;
    private static string? _initializedLibraryPath;
    private static string _lastHardwareFormats = string.Empty;

    private readonly AVCodecContext* _codecContext;
    private readonly AVPacket* _packet;
    private readonly AVFrame* _frame;
    private readonly AVFrame* _softwareTransferFrame;
    private readonly bool _useD3D11Hardware;
    private byte[]? _codecConfiguration;
    private long _sequence;
    private bool _disposed;

    public H264StreamDecoder(string libraryPath, bool useD3D11Hardware = false)
    {
        InitializeBindings(libraryPath);
        _useD3D11Hardware = useD3D11Hardware;
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null)
        {
            throw new InvalidOperationException("FFmpeg H.264 解码器不可用。");
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _softwareTransferFrame = useD3D11Hardware ? ffmpeg.av_frame_alloc() : null;
        if (_codecContext is null ||
            _packet is null ||
            _frame is null ||
            (useD3D11Hardware && _softwareTransferFrame is null))
        {
            Dispose();
            throw new InvalidOperationException("无法分配 FFmpeg H.264 解码器资源。");
        }

        if (useD3D11Hardware)
        {
            AVBufferRef* deviceContext = null;
            Check(
                ffmpeg.av_hwdevice_ctx_create(
                    &deviceContext,
                    AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                    null,
                    null,
                    0),
                "创建 D3D11VA 解码设备");
            _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(deviceContext);
            ffmpeg.av_buffer_unref(&deviceContext);
            if (_codecContext->hw_device_ctx is null)
            {
                Dispose();
                throw new InvalidOperationException("无法引用 D3D11VA 解码设备。");
            }
            _codecContext->get_format = HardwareFormatSelector;
        }

        Check(ffmpeg.avcodec_open2(_codecContext, codec, null), "打开 H.264 解码器");
    }

    public void Decode(
        Stream stream,
        Action<AndroidVideoFrame> frameHandler,
        CancellationToken cancellationToken,
        Action? mediaPacketHandler = null,
        Action? decodedFrameHandler = null,
        Action<TimeSpan>? pixelConversionHandler = null,
        Action<TimeSpan>? hardwareTransferHandler = null)
    {
        Span<byte> header = stackalloc byte[12];
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ReadExactly(stream, header, cancellationToken))
            {
                break;
            }

            var ptsAndFlags = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
            var packetSize = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[8..]));
            if (packetSize is <= 0 or > 16 * 1024 * 1024)
            {
                throw new InvalidDataException($"scrcpy 返回了无效的 H.264 数据包长度：{packetSize}。");
            }

            ffmpeg.av_packet_unref(_packet);
            Check(ffmpeg.av_new_packet(_packet, packetSize), "分配 H.264 数据包");
            if (!ReadExactly(stream, new Span<byte>(_packet->data, packetSize), cancellationToken))
            {
                throw new EndOfStreamException("scrcpy H.264 数据包在传输完成前结束。");
            }

            var isConfig = (ptsAndFlags & (1UL << 62)) != 0;
            var isKeyFrame = (ptsAndFlags & (1UL << 61)) != 0;
            if (isConfig)
            {
                _codecConfiguration = new Span<byte>(_packet->data, packetSize).ToArray();
                continue;
            }
            mediaPacketHandler?.Invoke();
            if (_codecConfiguration is { Length: > 0 } configuration)
            {
                Check(ffmpeg.av_grow_packet(_packet, configuration.Length), "合并 H.264 配置数据");
                var media = new Span<byte>(_packet->data, packetSize);
                media.CopyTo(new Span<byte>(_packet->data + configuration.Length, packetSize));
                configuration.CopyTo(new Span<byte>(_packet->data, configuration.Length));
                _codecConfiguration = null;
            }

            _packet->pts = isConfig
                ? ffmpeg.AV_NOPTS_VALUE
                : (long)(ptsAndFlags & 0x1FFF_FFFF_FFFF_FFFFUL);
            _packet->dts = _packet->pts;
            if (isKeyFrame)
            {
                _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
            }
            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                Check(
                    sendResult,
                    _useD3D11Hardware
                        ? $"提交 H.264 数据包（D3D11VA 候选格式：{LastHardwareFormats}）"
                        : "提交 H.264 数据包");
            }

            while (true)
            {
                var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                    receiveResult == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                Check(receiveResult, "解码 H.264 帧");
                decodedFrameHandler?.Invoke();
                var sourceFrame = _frame;
                if (_useD3D11Hardware && IsHardwareFrame(_frame))
                {
                    ffmpeg.av_frame_unref(_softwareTransferFrame);
                    var transferStarted = Stopwatch.GetTimestamp();
                    Check(
                        ffmpeg.av_hwframe_transfer_data(_softwareTransferFrame, _frame, 0),
                        "从 D3D11VA 纹理回读视频帧");
                    hardwareTransferHandler?.Invoke(Stopwatch.GetElapsedTime(transferStarted));
                    sourceFrame = _softwareTransferFrame;
                }
                var conversionStarted = Stopwatch.GetTimestamp();
                var frame = ConvertFrame(sourceFrame, Interlocked.Increment(ref _sequence));
                pixelConversionHandler?.Invoke(Stopwatch.GetElapsedTime(conversionStarted));
                frameHandler(frame);
                if (sourceFrame == _softwareTransferFrame)
                {
                    ffmpeg.av_frame_unref(_softwareTransferFrame);
                }
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_frame is not null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
        }
        if (_softwareTransferFrame is not null)
        {
            var frame = _softwareTransferFrame;
            ffmpeg.av_frame_free(&frame);
        }
        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
        }
        if (_codecContext is not null)
        {
            var context = _codecContext;
            ffmpeg.avcodec_free_context(&context);
        }
    }

    private static AVPixelFormat SelectHardwareFormat(
        AVCodecContext* codecContext,
        AVPixelFormat* formats)
    {
        var offered = new List<string>();
        for (var current = formats; *current != AVPixelFormat.AV_PIX_FMT_NONE; current++)
        {
            offered.Add(current->ToString());
            if (*current == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                SetLastHardwareFormats(offered);
                return *current;
            }
        }
        SetLastHardwareFormats(offered);
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static string LastHardwareFormats
    {
        get
        {
            lock (HardwareFormatSync)
            {
                return _lastHardwareFormats;
            }
        }
    }

    private static void SetLastHardwareFormats(IReadOnlyList<string> formats)
    {
        lock (HardwareFormatSync)
        {
            _lastHardwareFormats = string.Join(", ", formats);
        }
    }

    private static bool IsHardwareFrame(AVFrame* frame) =>
        (AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11;

    private static AndroidVideoFrame ConvertFrame(AVFrame* frame, long sequence)
    {
        var width = frame->width;
        var height = frame->height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("FFmpeg 返回了无效的手机画面尺寸。");
        }

        var format = (AVPixelFormat)frame->format;
        if (format is not AVPixelFormat.AV_PIX_FMT_YUV420P and
            not AVPixelFormat.AV_PIX_FMT_YUVJ420P and
            not AVPixelFormat.AV_PIX_FMT_NV12 and
            not AVPixelFormat.AV_PIX_FMT_NV21)
        {
            throw new NotSupportedException($"暂不支持 FFmpeg 像素格式 {format}。");
        }

        var fullRange = format == AVPixelFormat.AV_PIX_FMT_YUVJ420P ||
                        frame->color_range == AVColorRange.AVCOL_RANGE_JPEG;
        var output = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        fixed (byte* destination = output)
        {
            for (var y = 0; y < height; y++)
            {
                var yRow = frame->data[0] + y * frame->linesize[0];
                var chromaRow = y / 2;
                var uRow = frame->data[1] + chromaRow * frame->linesize[1];
                var vRow = format is AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P
                    ? frame->data[2] + chromaRow * frame->linesize[2]
                    : null;
                var target = destination + y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var luma = yRow[x];
                    int u;
                    int v;
                    if (format is AVPixelFormat.AV_PIX_FMT_NV12 or AVPixelFormat.AV_PIX_FMT_NV21)
                    {
                        var chroma = (x / 2) * 2;
                        var first = uRow[chroma];
                        var second = uRow[chroma + 1];
                        u = format == AVPixelFormat.AV_PIX_FMT_NV12 ? first : second;
                        v = format == AVPixelFormat.AV_PIX_FMT_NV12 ? second : first;
                    }
                    else
                    {
                        u = uRow[x / 2];
                        v = vRow![x / 2];
                    }

                    WriteBgra(target + x * 4, luma, u, v, fullRange);
                }
            }
        }

        return new AndroidVideoFrame(output, width, height, sequence);
    }

    private static void WriteBgra(byte* target, int y, int u, int v, bool fullRange)
    {
        var d = u - 128;
        var e = v - 128;
        int r;
        int g;
        int b;
        if (fullRange)
        {
            r = y + ((359 * e) >> 8);
            g = y - ((88 * d + 183 * e) >> 8);
            b = y + ((454 * d) >> 8);
        }
        else
        {
            var c = Math.Max(0, y - 16);
            r = (298 * c + 409 * e + 128) >> 8;
            g = (298 * c - 100 * d - 208 * e + 128) >> 8;
            b = (298 * c + 516 * d + 128) >> 8;
        }

        target[0] = ClampByte(b);
        target[1] = ClampByte(g);
        target[2] = ClampByte(r);
        target[3] = 255;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);

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
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("scrcpy 数据流在消息中途结束。");
            }
            offset += read;
        }

        return true;
    }

    private static void InitializeBindings(string libraryPath)
    {
        var fullPath = Path.GetFullPath(libraryPath);
        lock (InitializationSync)
        {
            if (_initializedLibraryPath is not null)
            {
                if (!string.Equals(_initializedLibraryPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"FFmpeg 已从 {_initializedLibraryPath} 加载，无法切换到 {fullPath}。");
                }
                return;
            }

            DynamicallyLoadedBindings.LibrariesPath = fullPath;
            DynamicallyLoadedBindings.Initialize();
            _initializedLibraryPath = fullPath;
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"{operation}失败，FFmpeg 错误码 {result}。");
        }
    }
}
