using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.App.SteamVR;

public interface IWpfSpatialOverlayHost
{
    Task<long> ShowWindowAsync(
        Window window,
        WpfSpatialOverlayOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<bool> CloseWindowAsync(
        long overlayId,
        CancellationToken cancellationToken = default);

    void InvalidateWindow(long overlayId);
}

public sealed record WpfSpatialOverlayOptions
{
    public string Name { get; init; } = "WPF Window";

    public float WidthMeters { get; init; } = 0.72f;

    public float DistanceMeters { get; init; } = 0.72f;

    public bool CanGrab { get; init; } = true;

    public int MaximumFramesPerSecond { get; init; } = 30;

    internal WpfSpatialOverlayOptions Validated() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "WPF Window" : Name.Trim(),
        WidthMeters = Math.Clamp(WidthMeters, 0.12f, 2.5f),
        DistanceMeters = Math.Clamp(DistanceMeters, 0.2f, 3f),
        MaximumFramesPerSecond = Math.Clamp(MaximumFramesPerSecond, 1, 60)
    };
}

internal sealed class WpfWindowOverlaySource : IDisposable
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmMouseWheel = 0x020A;
    private const nuint MkLeftButton = 0x0001;

    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private readonly nint _windowHandle;
    private readonly int _fallbackClientWidth;
    private readonly int _fallbackClientHeight;
    private NormalizedPoint? _lastPointer;
    private bool _leftButtonDown;

    public WpfWindowOverlaySource(Window window, WpfSpatialOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _dispatcher = window.Dispatcher;
        Options = options.Validated();
        var metrics = Invoke(ReadWindowMetrics);
        _windowHandle = metrics.Handle;
        AspectRatio = metrics.AspectRatio;
        _fallbackClientWidth = metrics.ClientWidth;
        _fallbackClientHeight = metrics.ClientHeight;
    }

    public WpfSpatialOverlayOptions Options { get; }

    public double AspectRatio { get; }

    public TimeSpan FrameInterval =>
        TimeSpan.FromSeconds(1d / Options.MaximumFramesPerSecond);

    public BitmapSource Render(int pixelWidth, int pixelHeight) => Invoke(() =>
    {
        var visual = ResolveVisual();
        EnsureLayout(visual, _fallbackClientWidth, _fallbackClientHeight);
        var drawingVisual = new DrawingVisual();
        using (var drawing = drawingVisual.RenderOpen())
        {
            drawing.DrawRectangle(
                new VisualBrush(visual)
                {
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                },
                null,
                new Rect(0, 0, pixelWidth, pixelHeight));
        }

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();
        return (BitmapSource)bitmap;
    });

    public void PointerMove(NormalizedPoint point)
    {
        var clamped = point.Clamp();
        if (_lastPointer is { } previous &&
            MathF.Abs(previous.X - clamped.X) < 0.0005f &&
            MathF.Abs(previous.Y - clamped.Y) < 0.0005f)
        {
            return;
        }

        _lastPointer = clamped;
        var client = ToClientPoint(clamped);
        _ = PostMessage(
            _windowHandle,
            WmMouseMove,
            _leftButtonDown ? MkLeftButton : 0,
            PackPoint(client.X, client.Y));
    }

    public void PointerDown(NormalizedPoint point)
    {
        PointerMove(point);
        var client = ToClientPoint(point.Clamp());
        _leftButtonDown = true;
        _ = PostMessage(
            _windowHandle,
            WmLeftButtonDown,
            MkLeftButton,
            PackPoint(client.X, client.Y));
    }

    public void PointerUp(NormalizedPoint point)
    {
        PointerMove(point);
        var client = ToClientPoint(point.Clamp());
        _leftButtonDown = false;
        _ = PostMessage(
            _windowHandle,
            WmLeftButtonUp,
            0,
            PackPoint(client.X, client.Y));
    }

    public void PointerWheel(NormalizedPoint point, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        PointerMove(point);
        var screen = ToClientPoint(point.Clamp());
        _ = ClientToScreen(_windowHandle, ref screen);
        var wheel = unchecked((nuint)((uint)(unchecked((ushort)(short)delta)) << 16));
        _ = PostMessage(
            _windowHandle,
            WmMouseWheel,
            wheel,
            PackPoint(screen.X, screen.Y));
    }

    public void CancelPointer()
    {
        if (_leftButtonDown)
        {
            PointerUp(_lastPointer ?? new NormalizedPoint(0.5f, 0.5f));
        }
    }

    public void Dispose() => CancelPointer();

    private WpfWindowMetrics ReadWindowMetrics()
    {
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        var visual = ResolveVisual();
        var requestedWidth = double.IsFinite(_window.Width) && _window.Width > 1
            ? _window.Width
            : 800;
        var requestedHeight = double.IsFinite(_window.Height) && _window.Height > 1
            ? _window.Height
            : 600;
        EnsureLayout(visual, requestedWidth, requestedHeight);
        var width = visual is FrameworkElement element && element.ActualWidth > 1
            ? element.ActualWidth
            : Math.Max(1, _window.ActualWidth > 1 ? _window.ActualWidth : _window.Width);
        var height = visual is FrameworkElement content && content.ActualHeight > 1
            ? content.ActualHeight
            : Math.Max(1, _window.ActualHeight > 1 ? _window.ActualHeight : _window.Height);
        if (!double.IsFinite(width) || width <= 0)
        {
            width = 800;
        }
        if (!double.IsFinite(height) || height <= 0)
        {
            height = 600;
        }
        return new WpfWindowMetrics(
            handle,
            Math.Clamp(width / height, 0.2, 5),
            Math.Max(1, (int)Math.Round(width)),
            Math.Max(1, (int)Math.Round(height)));
    }

    private Visual ResolveVisual() => _window.Content as Visual ?? _window;

    private static void EnsureLayout(Visual visual, double fallbackWidth, double fallbackHeight)
    {
        if (visual is not FrameworkElement element ||
            (element.ActualWidth > 1 && element.ActualHeight > 1))
        {
            return;
        }

        var width = double.IsFinite(element.Width) && element.Width > 1
            ? element.Width
            : Math.Max(1, fallbackWidth);
        var height = double.IsFinite(element.Height) && element.Height > 1
            ? element.Height
            : Math.Max(1, fallbackHeight);
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private NativePoint ToClientPoint(NormalizedPoint point)
    {
        var hasBounds = GetClientRect(_windowHandle, out var bounds);
        var width = hasBounds && bounds.Right - bounds.Left > 1
            ? bounds.Right - bounds.Left
            : _fallbackClientWidth;
        var height = hasBounds && bounds.Bottom - bounds.Top > 1
            ? bounds.Bottom - bounds.Top
            : _fallbackClientHeight;
        return new NativePoint
        {
            X = Math.Clamp((int)Math.Round(point.X * (width - 1)), 0, width - 1),
            Y = Math.Clamp((int)Math.Round(point.Y * (height - 1)), 0, height - 1)
        };
    }

    private T Invoke<T>(Func<T> action) => _dispatcher.CheckAccess()
        ? action()
        : _dispatcher.Invoke(action);

    private static nint PackPoint(int x, int y) =>
        unchecked((nint)((uint)(ushort)x | ((uint)(ushort)y << 16)));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
}

internal readonly record struct WpfWindowMetrics(
    nint Handle,
    double AspectRatio,
    int ClientWidth,
    int ClientHeight);

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal abstract record WpfOverlayCommand;

internal sealed record ShowWpfOverlayCommand(
    Window Window,
    WpfSpatialOverlayOptions Options,
    TaskCompletionSource<long> Completion) : WpfOverlayCommand;

internal sealed record CloseWpfOverlayCommand(
    long OverlayId,
    TaskCompletionSource<bool> Completion) : WpfOverlayCommand;

internal sealed record InvalidateWpfOverlayCommand(long OverlayId) : WpfOverlayCommand;
