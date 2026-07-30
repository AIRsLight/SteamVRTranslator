using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpGen.Runtime;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Valve.VR;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SteamVRTranslator.App.Capture;

internal sealed class SteamVrCompositorCaptureService : IDisposable
{
    private const int MaxOutputWidth = 3072;
    private const int MaxOutputHeight = 2048;
    private const int JpegQuality = 92;
    private readonly AppLog _log;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private MirrorEyeSource? _leftMirror;
    private MirrorEyeSource? _rightMirror;

    public SteamVrCompositorCaptureService(AppLog log)
    {
        _log = log;
    }

    public byte[] CaptureJpeg(
        SpatialSelectionPlane plane,
        string compositionMode,
        string diagnosticTag = "")
    {
        var stopwatch = Stopwatch.StartNew();
        var eye = ParseCaptureEye(compositionMode);
        EnsureDevice();
        var mirror = EnsureMirror(eye, out var mirrorCreated);
        var frameTiming = WaitForFreshCompositorFrame(
            diagnosticTag,
            mirrorCreated ? 2 : 1);
        var frame = CaptureEye(mirror);
        for (var retry = 1; retry <= 2 && IsBlankMirrorFrame(frame, out var average); retry++)
        {
            _log.Warning(
                $"{diagnosticTag} [capture] 镜像纹理接近纯黑：" +
                $"采样平均值={average:F2}，准备等待下一帧重试 {retry}/2。");
            frameTiming = WaitForFreshCompositorFrame(diagnosticTag, 1);
            frame = CaptureEye(mirror);
        }

        var hmdPose = frameTiming is { m_HmdPose: { bPoseIsValid: true } }
            ? Convert(frameTiming.Value.m_HmdPose.mDeviceToAbsoluteTracking)
            : ReadHmdPose();
        var projection = CreateEyeProjection(eye, hmdPose);
        var quad = ProjectPlane(plane, projection);
        _log.Info(
            $"{diagnosticTag} [capture] 空间框四角投影：" +
            $"眼睛={EyeName(eye)}，四角={Describe(quad)}，" +
            $"姿态帧={frameTiming?.m_nFrameIndex.ToString() ?? "<实时回退>"}");

        var result = RectifySingleEye(
            plane,
            frame,
            projection,
            quad,
            eye,
            diagnosticTag);
        _log.Info(
            $"{diagnosticTag} [capture] 单眼纹理捕获完成：" +
            $"眼睛={EyeName(eye)}，纹理={frame.Width}x{frame.Height}，" +
            $"JPEG(Q{JpegQuality})={result.Length:N0} bytes，阶段耗时={stopwatch.Elapsed.TotalMilliseconds:F0} ms");
        return result;
    }

    private static bool IsBlankMirrorFrame(EyeFrame frame, out double average)
    {
        long sum = 0;
        long channels = 0;
        const int sampleStep = 64;
        for (var y = 0; y < frame.Height; y += sampleStep)
        {
            for (var x = 0; x < frame.Width; x += sampleStep)
            {
                var offset = (y * frame.Stride) + (x * 4);
                sum += frame.Pixels[offset] + frame.Pixels[offset + 1] + frame.Pixels[offset + 2];
                channels += 3;
            }
        }

        average = channels == 0 ? 0 : sum / (double)channels;
        return average < 0.5;
    }

    public void Dispose()
    {
        ReleaseOpenVrResources();
        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;
    }

    public void ReleaseOpenVrResources()
    {
        _leftMirror?.Dispose();
        _rightMirror?.Dispose();
        _leftMirror = null;
        _rightMirror = null;
    }

    private void EnsureDevice()
    {
        if (_device is not null && _context is not null)
        {
            return;
        }

        SteamVrD3D11DeviceFactory.Create(
            _log,
            "SteamVR 捕获",
            out var device,
            out _,
            out var context);
        _device = device;
        _context = context;
    }

    private MirrorEyeSource EnsureMirror(EVREye eye, out bool created)
    {
        var existing = eye == EVREye.Eye_Left ? _leftMirror : _rightMirror;
        if (existing is not null)
        {
            created = false;
            return existing;
        }

        var mirror = AcquireMirror(eye);
        if (eye == EVREye.Eye_Left)
        {
            _leftMirror = mirror;
        }
        else
        {
            _rightMirror = mirror;
        }

        created = true;
        return mirror;
    }

    private MirrorEyeSource AcquireMirror(EVREye eye)
    {
        var nativeView = IntPtr.Zero;
        var error = OpenVR.Compositor.GetMirrorTextureD3D11(
            eye,
            _device!.NativePointer,
            ref nativeView);
        if (error != EVRCompositorError.None || nativeView == IntPtr.Zero)
        {
            throw new InvalidOperationException($"获取 SteamVR {eye} 合成纹理失败：{error}");
        }

        var view = new ID3D11ShaderResourceView(nativeView);
        ID3D11Resource? resource = null;
        ID3D11Texture2D? source = null;
        try
        {
            resource = view.Resource;
            source = resource.QueryInterface<ID3D11Texture2D>();
            var sourceDescription = source.Description;
            _log.Info(
                $"SteamVR {EyeName(eye)}镜像纹理已持有：" +
                $"{sourceDescription.Width}x{sourceDescription.Height}，" +
                $"格式={sourceDescription.Format}，NativeView=0x{nativeView.ToInt64():X}");
            ValidateFormat(sourceDescription.Format);
            return new MirrorEyeSource(
                nativeView,
                view,
                resource,
                source,
                sourceDescription,
                eye);
        }
        catch
        {
            source?.Dispose();
            resource?.Dispose();
            view.NativePointer = IntPtr.Zero;
            OpenVR.Compositor.ReleaseMirrorTextureD3D11(nativeView);
            throw;
        }
    }

    private EyeFrame CaptureEye(MirrorEyeSource mirror)
    {
        var description = mirror.Description;
        using var staging = _device!.CreateTexture2D(new Texture2DDescription(
            description.Format,
            description.Width,
            description.Height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read));
        _context!.CopyResource(staging, mirror.Source);
        _context.Flush();
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            return CopyMappedFrame(
                mapped,
                checked((int)description.Width),
                checked((int)description.Height),
                description.Format);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    private Compositor_FrameTiming? WaitForFreshCompositorFrame(
        string diagnosticTag,
        int requiredAdvances)
    {
        if (!TryReadFrameTiming(out var initial))
        {
            _log.Warning($"{diagnosticTag} [capture] 无法读取合成器帧时序，等待 20 ms 后捕获。");
            Thread.Sleep(20);
            return TryReadFrameTiming(out var fallback) ? fallback : null;
        }

        var current = initial;
        var previousFrameIndex = initial.m_nFrameIndex;
        var advances = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            Thread.Sleep(1);
            if (!TryReadFrameTiming(out current))
            {
                continue;
            }

            if (current.m_nFrameIndex != previousFrameIndex)
            {
                previousFrameIndex = current.m_nFrameIndex;
                advances++;
                if (advances >= requiredAdvances)
                {
                    _log.Info(
                        $"{diagnosticTag} [capture] 帧同步：" +
                        $"{initial.m_nFrameIndex} -> {current.m_nFrameIndex}，" +
                        $"推进={advances}，等待={stopwatch.Elapsed.TotalMilliseconds:F1} ms，" +
                        $"呈现={current.m_nNumFramePresents}，丢帧={current.m_nNumDroppedFrames}");
                    return current;
                }
            }
        }

        _log.Warning(
            $"{diagnosticTag} [capture] 帧在 500 ms 内未满足推进要求：" +
            $"要求={requiredAdvances}，实际={advances}，" +
            $"继续读取当前帧 {current.m_nFrameIndex}。");
        return current;
    }

    private static bool TryReadFrameTiming(out Compositor_FrameTiming timing)
    {
        timing = new Compositor_FrameTiming
        {
            m_nSize = unchecked((uint)Marshal.SizeOf<Compositor_FrameTiming>())
        };
        return OpenVR.Compositor.GetFrameTiming(ref timing, 0);
    }

    private static EyeFrame CopyMappedFrame(
        MappedSubresource mapped,
        int width,
        int height,
        Format format)
    {
        var outputStride = checked(width * 4);
        var output = new byte[checked(outputStride * height)];
        var sourceRow = new byte[outputStride];
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(
                IntPtr.Add(mapped.DataPointer, checked(y * (int)mapped.RowPitch)),
                sourceRow,
                0,
                outputStride);
            var destinationOffset = y * outputStride;
            if (format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or
                Format.B8G8R8A8_Typeless)
            {
                Buffer.BlockCopy(sourceRow, 0, output, destinationOffset, outputStride);
                for (var x = 3; x < outputStride; x += 4)
                {
                    output[destinationOffset + x] = 255;
                }

                continue;
            }

            for (var x = 0; x < outputStride; x += 4)
            {
                output[destinationOffset + x] = sourceRow[x + 2];
                output[destinationOffset + x + 1] = sourceRow[x + 1];
                output[destinationOffset + x + 2] = sourceRow[x];
                output[destinationOffset + x + 3] = 255;
            }
        }

        return new EyeFrame(width, height, outputStride, output);
    }

    private byte[] RectifySingleEye(
        SpatialSelectionPlane plane,
        EyeFrame frame,
        EyeProjection projection,
        ProjectedQuad quad,
        EVREye eye,
        string diagnosticTag)
    {
        var (width, height) = DetermineOutputSize(plane, frame, quad);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var coverage = EstimateCoverage(plane, projection);
        long copiedPixels = 0;
        long missingPixels = 0;
        long luminanceSum = 0;
        var topLeft = plane.Center - (plane.Right * (plane.Width / 2f)) +
                      (plane.Up * (plane.Height / 2f));
        var horizontalStep = width > 1
            ? plane.Right * (plane.Width / (width - 1f))
            : default;
        var verticalStep = height > 1
            ? plane.Up * (-plane.Height / (height - 1f))
            : default;

        for (var y = 0; y < height; y++)
        {
            var point = topLeft + (verticalStep * y);
            for (var x = 0; x < width; x++)
            {
                var destinationOffset = (y * stride) + (x * 4);
                if (TryProjectAndSample(frame, projection, point, out var pixel))
                {
                    WritePixel(pixels, destinationOffset, pixel);
                    copiedPixels++;
                    luminanceSum += pixel.Blue + pixel.Green + pixel.Red;
                }
                else
                {
                    pixels[destinationOffset + 3] = 255;
                    missingPixels++;
                }

                point += horizontalStep;
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        var totalPixels = (long)width * height;
        var averageChannel = copiedPixels == 0 ? 0 : luminanceSum / (copiedPixels * 3d);
        _log.Info(
            $"{diagnosticTag} [capture] 单眼透视校正：眼睛={EyeName(eye)}，" +
            $"输出={width}x{height}，覆盖率={coverage:P1}，" +
            $"有效像素={copiedPixels / (double)totalPixels:P1}，" +
            $"缺失={missingPixels / (double)totalPixels:P1}，平均通道值={averageChannel:F1}");
        return EncodeJpeg(bitmap);
    }

    internal static byte[] EncodeJpeg(BitmapSource bitmap, int quality = JpegQuality)
    {
        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = Math.Clamp(quality, 1, 100)
        };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static bool TryProjectAndSample(
        EyeFrame frame,
        EyeProjection projection,
        Vector3f point,
        out BgraPixel pixel)
    {
        if (!TryProject(point, projection, out var projected) ||
            projected.X < 0f || projected.X > 1f || projected.Y < 0f || projected.Y > 1f)
        {
            pixel = default;
            return false;
        }

        var sourceX = Math.Clamp((int)MathF.Round(projected.X * (frame.Width - 1)), 0, frame.Width - 1);
        var sourceY = Math.Clamp((int)MathF.Round(projected.Y * (frame.Height - 1)), 0, frame.Height - 1);
        var sourceOffset = (sourceY * frame.Stride) + (sourceX * 4);
        pixel = new BgraPixel(
            frame.Pixels[sourceOffset],
            frame.Pixels[sourceOffset + 1],
            frame.Pixels[sourceOffset + 2]);
        return true;
    }

    private static void WritePixel(byte[] destination, int offset, BgraPixel pixel)
    {
        destination[offset] = pixel.Blue;
        destination[offset + 1] = pixel.Green;
        destination[offset + 2] = pixel.Red;
        destination[offset + 3] = 255;
    }

    private static RigidTransform3x4 ReadHmdPose()
    {
        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            OpenVR.Compositor.GetTrackingSpace(),
            0f,
            poses);
        var pose = poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!pose.bPoseIsValid)
        {
            throw new InvalidOperationException("捕获时头显追踪姿态不可用。");
        }

        return Convert(pose.mDeviceToAbsoluteTracking);
    }

    private static EyeProjection CreateEyeProjection(EVREye eye, RigidTransform3x4 hmdToAbsolute)
    {
        var eyeToHead = Convert(OpenVR.System.GetEyeToHeadTransform(eye));
        var eyeToAbsolute = RigidTransform3x4.Multiply(hmdToAbsolute, eyeToHead);
        var projection = OpenVR.System.GetProjectionMatrix(eye, 0.01f, 100f);
        return new EyeProjection(eyeToAbsolute, projection);
    }

    private static ProjectedQuad ProjectPlane(SpatialSelectionPlane plane, EyeProjection projection)
    {
        var points = new NormalizedPoint[4];
        for (var index = 0; index < points.Length; index++)
        {
            if (!TryProject(plane.Corners[index], projection, out points[index]))
            {
                throw new InvalidOperationException("空间框选平面位于头显视野后方。");
            }
        }

        return new ProjectedQuad(points[0], points[1], points[2], points[3]);
    }

    private static bool TryProject(
        Vector3f absolutePoint,
        EyeProjection eyeProjection,
        out NormalizedPoint result)
    {
        var point = eyeProjection.EyeToAbsolute.InverseTransformPoint(absolutePoint);
        var projection = eyeProjection.Projection;
        var clipX = (projection.m0 * point.X) + (projection.m1 * point.Y) +
                    (projection.m2 * point.Z) + projection.m3;
        var clipY = (projection.m4 * point.X) + (projection.m5 * point.Y) +
                    (projection.m6 * point.Z) + projection.m7;
        var clipW = (projection.m12 * point.X) + (projection.m13 * point.Y) +
                    (projection.m14 * point.Z) + projection.m15;
        if (clipW <= 0.0001f)
        {
            result = default;
            return false;
        }

        result = new NormalizedPoint(
            ((clipX / clipW) + 1f) * 0.5f,
            (1f - (clipY / clipW)) * 0.5f);
        return true;
    }

    private static (int Width, int Height) DetermineOutputSize(
        SpatialSelectionPlane plane,
        EyeFrame frame,
        ProjectedQuad quad)
    {
        var pixelsPerMeter = EstimatePixelsPerMeter(plane, frame, quad);
        var width = Math.Max(1, (int)MathF.Ceiling(plane.Width * pixelsPerMeter));
        var height = Math.Max(1, (int)MathF.Ceiling(plane.Height * pixelsPerMeter));
        var scale = Math.Min(
            1d,
            Math.Min(MaxOutputWidth / (double)width, MaxOutputHeight / (double)height));
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static float EstimatePixelsPerMeter(
        SpatialSelectionPlane plane,
        EyeFrame frame,
        ProjectedQuad quad)
    {
        var horizontalPixels = (
            PixelDistance(quad.TopLeft, quad.TopRight, frame) +
            PixelDistance(quad.BottomLeft, quad.BottomRight, frame)) / 2f;
        var verticalPixels = (
            PixelDistance(quad.TopLeft, quad.BottomLeft, frame) +
            PixelDistance(quad.TopRight, quad.BottomRight, frame)) / 2f;
        return Math.Max(horizontalPixels / plane.Width, verticalPixels / plane.Height);
    }

    private static float PixelDistance(NormalizedPoint first, NormalizedPoint second, EyeFrame frame)
    {
        var x = (second.X - first.X) * frame.Width;
        var y = (second.Y - first.Y) * frame.Height;
        return MathF.Sqrt((x * x) + (y * y));
    }

    private static float EstimateCoverage(SpatialSelectionPlane plane, EyeProjection projection)
    {
        const int sampleCount = 17;
        var visible = 0;
        for (var y = 0; y < sampleCount; y++)
        {
            var vertical = y / (sampleCount - 1f);
            for (var x = 0; x < sampleCount; x++)
            {
                var horizontal = x / (sampleCount - 1f);
                var point = plane.Center +
                            (plane.Right * ((horizontal - 0.5f) * plane.Width)) +
                            (plane.Up * ((0.5f - vertical) * plane.Height));
                if (TryProject(point, projection, out var projected) &&
                    projected.X >= 0f && projected.X <= 1f &&
                    projected.Y >= 0f && projected.Y <= 1f)
                {
                    visible++;
                }
            }
        }

        return visible / (float)(sampleCount * sampleCount);
    }

    private static void ValidateFormat(Format format)
    {
        if (format is not (Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or
            Format.R8G8B8A8_Typeless or Format.B8G8R8A8_UNorm or
            Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8A8_Typeless))
        {
            throw new NotSupportedException($"暂不支持 SteamVR 合成纹理格式：{format}");
        }
    }

    internal static EVREye ParseCaptureEye(string value) =>
        value switch
        {
            "left-eye" => EVREye.Eye_Left,
            "right-eye" => EVREye.Eye_Right,
            // Migrate configurations written by the earlier stereo implementation.
            "primary-eye" or "both-eyes-blend" => EVREye.Eye_Left,
            _ => throw new InvalidOperationException($"不支持的单眼捕获模式：{value}")
        };

    private static string EyeName(EVREye eye) =>
        eye == EVREye.Eye_Left ? "左眼" : "右眼";

    private static RigidTransform3x4 Convert(HmdMatrix34_t matrix) =>
        new(
            matrix.m0, matrix.m1, matrix.m2, matrix.m3,
            matrix.m4, matrix.m5, matrix.m6, matrix.m7,
            matrix.m8, matrix.m9, matrix.m10, matrix.m11);

    private static string Describe(ProjectedQuad quad) =>
        $"TL({quad.TopLeft.X:F3},{quad.TopLeft.Y:F3}) " +
        $"TR({quad.TopRight.X:F3},{quad.TopRight.Y:F3}) " +
        $"BL({quad.BottomLeft.X:F3},{quad.BottomLeft.Y:F3}) " +
        $"BR({quad.BottomRight.X:F3},{quad.BottomRight.Y:F3})";

    private sealed record EyeFrame(int Width, int Height, int Stride, byte[] Pixels);

    private readonly record struct BgraPixel(byte Blue, byte Green, byte Red);

    private readonly record struct EyeProjection(
        RigidTransform3x4 EyeToAbsolute,
        HmdMatrix44_t Projection);

    private readonly record struct ProjectedQuad(
        NormalizedPoint TopLeft,
        NormalizedPoint TopRight,
        NormalizedPoint BottomLeft,
        NormalizedPoint BottomRight);

    private sealed class MirrorEyeSource : IDisposable
    {
        private readonly IntPtr _nativeView;
        private readonly ID3D11ShaderResourceView _view;
        private readonly ID3D11Resource _resource;

        public MirrorEyeSource(
            IntPtr nativeView,
            ID3D11ShaderResourceView view,
            ID3D11Resource resource,
            ID3D11Texture2D source,
            Texture2DDescription description,
            EVREye eye)
        {
            _nativeView = nativeView;
            _view = view;
            _resource = resource;
            Source = source;
            Description = description;
            Eye = eye;
        }

        public ID3D11Texture2D Source { get; }

        public Texture2DDescription Description { get; }

        public EVREye Eye { get; }

        public void Dispose()
        {
            Source.Dispose();
            _resource.Dispose();
            // OpenVR owns the SRV reference and requires its matching release call.
            _view.NativePointer = IntPtr.Zero;
            OpenVR.Compositor.ReleaseMirrorTextureD3D11(_nativeView);
        }
    }
}
