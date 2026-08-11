using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.Core.Selection;
using Valve.VR;

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

internal enum DirectOverlayPixelFormat
{
    Rgba,
    Bgra
}

internal sealed record DirectOverlayPixelRegion(
    float X,
    float Y,
    float Width,
    float Height)
{
    public static DirectOverlayPixelRegion Full { get; } = new(0f, 0f, 1f, 1f);

    public DirectOverlayPixelRegion Clamp()
    {
        var x = Math.Clamp(X, 0f, 0.9999f);
        var y = Math.Clamp(Y, 0f, 0.9999f);
        return new DirectOverlayPixelRegion(
            x,
            y,
            Math.Clamp(Width, 0.0001f, 1f - x),
            Math.Clamp(Height, 0.0001f, 1f - y));
    }
}

internal readonly record struct DirectOverlayPixelFrame(
    byte[] Pixels,
    int Width,
    int Height,
    long Sequence,
    DirectOverlayPixelFormat Format);

internal interface IWpfOverlayDirectPixelSource
{
    long LatestDirectPixelSequence { get; }

    DirectOverlayPixelRegion DirectPixelRegion { get; }

    bool TryGetLatestDirectPixelFrame(out DirectOverlayPixelFrame frame);
}

public sealed record WpfSpatialOverlayOptions
{
    public string Name { get; init; } = "WPF Window";

    public float WidthMeters { get; init; } = 0.72f;

    public float DistanceMeters { get; init; } = 0.72f;

    public WpfSpatialOverlayPlacement Placement { get; init; } = WpfSpatialOverlayPlacement.Head;

    public bool CanGrab { get; init; } = true;

    public bool ShowToolbarWhenGrabbed { get; init; } = true;

    public bool CloseWindowOnOverlayRemoval { get; init; }

    public int MaximumFramesPerSecond { get; init; } = 60;

    internal WpfSpatialOverlayOptions Validated() => this with
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "WPF Window" : Name.Trim(),
        WidthMeters = Math.Clamp(WidthMeters, 0.06f, 2.5f),
        DistanceMeters = Math.Clamp(DistanceMeters, 0.2f, 3f),
        MaximumFramesPerSecond = Math.Clamp(MaximumFramesPerSecond, 1, 120)
    };
}

public enum WpfSpatialOverlayPlacement
{
    Head,
    LeftHand
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
    private readonly IWpfOverlayPointerSink? _pointerSink;
    private readonly IWpfOverlayPointerHoverAware? _pointerHoverAware;
    private readonly IWpfOverlayPointerControlResolver? _pointerControlResolver;
    private readonly IWpfOverlayDirectPixelSource? _directPixelSource;
    private readonly int _fallbackClientWidth;
    private readonly int _fallbackClientHeight;
    private NormalizedPoint? _lastPointer;
    private bool _leftButtonDown;
    private bool _directPointerActive;
    private FrameworkElement? _directPressedControl;
    private NormalizedPoint? _directPressedAt;
    private ScrollViewer? _swipeScrollViewer;
    private byte[]? _pixelBuffer;
    private int _contentVersion = 1;
    private int _renderedContentVersion;
    private int _synchronizeBuffersRequested;
    private long _renderedDirectPixelSequence = long.MinValue;

    public WpfWindowOverlaySource(Window window, WpfSpatialOverlayOptions options)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _dispatcher = window.Dispatcher;
        _pointerSink = window as IWpfOverlayPointerSink;
        _pointerHoverAware = window as IWpfOverlayPointerHoverAware;
        _pointerControlResolver = window as IWpfOverlayPointerControlResolver;
        _directPixelSource = window as IWpfOverlayDirectPixelSource;
        Options = options.Validated();
        var metrics = Invoke(ReadWindowMetrics);
        _windowHandle = metrics.Handle;
        AspectRatio = metrics.AspectRatio;
        _fallbackClientWidth = metrics.ClientWidth;
        _fallbackClientHeight = metrics.ClientHeight;
        if (_window is IWpfOverlayInvalidationSource invalidationSource)
        {
            invalidationSource.OverlayInvalidated += OnOverlayInvalidated;
        }
    }

    public WpfSpatialOverlayOptions Options { get; }

    public double AspectRatio { get; }

    public int SourcePixelWidth => _fallbackClientWidth;

    public int SourcePixelHeight => _fallbackClientHeight;

    public TimeSpan FrameInterval =>
        TimeSpan.FromSeconds(1d / Options.MaximumFramesPerSecond);

    public bool NeedsRender =>
        Volatile.Read(ref _contentVersion) != Volatile.Read(ref _renderedContentVersion);

    public bool SynchronizeBuffersRequested =>
        Volatile.Read(ref _synchronizeBuffersRequested) != 0;

    public bool HasDirectPixelSource => _directPixelSource is not null;

    public bool NeedsDirectPixelRender =>
        _directPixelSource is { } source &&
        source.LatestDirectPixelSequence != long.MinValue &&
        source.LatestDirectPixelSequence != Volatile.Read(ref _renderedDirectPixelSequence);

    public DirectOverlayPixelRegion DirectPixelRegion =>
        _directPixelSource?.DirectPixelRegion.Clamp() ?? DirectOverlayPixelRegion.Full;

    public void Invalidate() => Interlocked.Increment(ref _contentVersion);

    public bool ConsumeBufferSynchronizationRequest() =>
        Interlocked.Exchange(ref _synchronizeBuffersRequested, 0) != 0;

    public bool TryGetLatestDirectPixelFrame(out DirectOverlayPixelFrame frame)
    {
        if (_directPixelSource is not null)
        {
            return _directPixelSource.TryGetLatestDirectPixelFrame(out frame);
        }

        frame = default;
        return false;
    }

    public void MarkDirectPixelFrameRendered(long sequence) =>
        Volatile.Write(ref _renderedDirectPixelSequence, sequence);

    internal byte[] GetReusablePixelBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        if (_pixelBuffer is null || _pixelBuffer.Length != length)
        {
            _pixelBuffer = GC.AllocateUninitializedArray<byte>(length);
        }
        return _pixelBuffer;
    }

    public BitmapSource Render(int pixelWidth, int pixelHeight, int renderScale = 1)
    {
        renderScale = Math.Max(1, renderScale);
        return RenderPixels(
            checked(pixelWidth * renderScale),
            checked(pixelHeight * renderScale),
            96d * renderScale);
    }

    public BitmapSource RenderPixels(int pixelWidth, int pixelHeight) =>
        RenderPixels(pixelWidth, pixelHeight, 96d);

    private BitmapSource RenderPixels(int pixelWidth, int pixelHeight, double dpi)
    {
        var renderingVersion = Volatile.Read(ref _contentVersion);
        var bitmap = Invoke(() =>
        {
            pixelWidth = Math.Max(1, pixelWidth);
            pixelHeight = Math.Max(1, pixelHeight);
            var visual = ResolveVisual();
            EnsureLayout(visual, _fallbackClientWidth, _fallbackClientHeight);
            // 喵~ 用窗口属性尺寸做源尺寸基准，与 ReadWindowMetrics 保持一致
            // 避免窗口被系统压缩后 Actual 偏离设计值导致纹理纵横比与 plane 不一致
            var sourceWidth = _fallbackClientWidth;
            var sourceHeight = _fallbackClientHeight;
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                dpi * pixelWidth / Math.Max(1, sourceWidth),
                dpi * pixelHeight / Math.Max(1, sourceHeight),
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return (BitmapSource)bitmap;
        });
        Volatile.Write(ref _renderedContentVersion, renderingVersion);
        return bitmap;
    }

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
        if (_pointerHoverAware is not null)
        {
            UpdatePointerHover(clamped);
        }
        if (_pointerSink is not null)
        {
            _pointerSink.PointerMove(clamped);
            return;
        }

        if (_directPointerActive)
        {
            _dispatcher.BeginInvoke(() => UpdateDirectPointer(clamped));
            return;
        }

        var client = ToClientPoint(clamped);
        _ = PostMessage(
            _windowHandle,
            WmMouseMove,
            _leftButtonDown ? MkLeftButton : 0,
            PackPoint(client.X, client.Y));
    }

    public string? PointerDown(NormalizedPoint point)
    {
        var clamped = point.Clamp();
        _swipeScrollViewer = null;
        if (_leftButtonDown)
        {
            CancelPointer();
        }
        PointerMove(clamped);
        _leftButtonDown = true;
        if (_pointerSink is not null)
        {
            RequestBufferSynchronization();
            return _pointerSink.PointerDown(clamped);
        }

        var directPointer = Invoke(() => BeginDirectPointer(clamped));
        _directPointerActive = directPointer.IsActive;
        if (_directPointerActive)
        {
            RequestBufferSynchronization();
            return directPointer.ControlName;
        }

        var client = ToClientPoint(clamped);
        _ = PostMessage(
            _windowHandle,
            WmLeftButtonDown,
            MkLeftButton,
            PackPoint(client.X, client.Y));
        return null;
    }

    public bool BeginPointerSwipe(NormalizedPoint point)
    {
        if (_pointerSink is not null)
        {
            return false;
        }

        var clamped = point.Clamp();
        var scrollViewer = Invoke(() =>
        {
            if (_directPressedControl is Slider)
            {
                return null;
            }

            return HitTestScrollableViewer(clamped);
        });
        if (scrollViewer is null)
        {
            return false;
        }

        CancelPointer();
        _swipeScrollViewer = scrollViewer;
        return true;
    }

    public bool PointerSwipe(double normalizedOffsetDelta)
    {
        if (_swipeScrollViewer is null || Math.Abs(normalizedOffsetDelta) < 0.00001)
        {
            return false;
        }

        var changed = Invoke(() =>
        {
            var scrollViewer = _swipeScrollViewer;
            if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0.01)
            {
                return false;
            }

            var visual = ResolveVisual();
            EnsureLayout(visual, _fallbackClientWidth, _fallbackClientHeight);
            var rootHeight = visual is FrameworkElement root && root.ActualHeight > 1
                ? root.ActualHeight
                : _fallbackClientHeight;
            var viewerHeight = scrollViewer.ActualHeight > 1
                ? scrollViewer.ActualHeight
                : rootHeight;
            var viewportUnits = scrollViewer.ViewportHeight > 0
                ? scrollViewer.ViewportHeight
                : viewerHeight;
            var offsetDelta = normalizedOffsetDelta * rootHeight * viewportUnits / viewerHeight;
            var previousOffset = scrollViewer.VerticalOffset;
            var nextOffset = Math.Clamp(
                previousOffset + offsetDelta,
                0,
                scrollViewer.ScrollableHeight);
            if (Math.Abs(nextOffset - previousOffset) < 0.001)
            {
                return false;
            }

            scrollViewer.ScrollToVerticalOffset(nextOffset);
            return true;
        });
        if (changed)
        {
            Invalidate();
        }
        return changed;
    }

    public void EndPointerSwipe() => _swipeScrollViewer = null;

    public bool? PointerUp(NormalizedPoint point)
    {
        var clamped = point.Clamp();
        if (!_leftButtonDown)
        {
            return false;
        }
        if (_pointerSink is not null)
        {
            _leftButtonDown = false;
            _lastPointer = clamped;
            var executed = _pointerSink.PointerUp(clamped);
            RequestBufferSynchronization();
            Invalidate();
            return executed;
        }

        if (_directPointerActive)
        {
            try
            {
                var executed = Invoke(() => CompleteDirectPointer(clamped));
                RequestBufferSynchronization();
                Invalidate();
                return executed;
            }
            finally
            {
                _directPointerActive = false;
                _leftButtonDown = false;
                _lastPointer = clamped;
            }
        }

        PointerMove(clamped);
        var client = ToClientPoint(clamped);
        _leftButtonDown = false;
        _ = PostMessage(
            _windowHandle,
            WmLeftButtonUp,
            0,
            PackPoint(client.X, client.Y));
        QueueInvalidateAfterInput();
        return null;
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
        QueueInvalidateAfterInput();
    }

    public void CancelPointer()
    {
        _swipeScrollViewer = null;
        if (_pointerSink is not null)
        {
            if (_leftButtonDown)
            {
                _pointerSink.PointerCancel();
            }
            _leftButtonDown = false;
            PointerLeave();
            return;
        }

        if (_directPointerActive)
        {
            Invoke(CancelDirectPointer);
            _directPointerActive = false;
            _leftButtonDown = false;
            PointerLeave();
            return;
        }

        if (_leftButtonDown)
        {
            PointerUp(_lastPointer ?? new NormalizedPoint(0.5f, 0.5f));
        }
        PointerLeave();
    }

    public void PointerLeave()
    {
        _lastPointer = null;
        if (_pointerHoverAware is not null)
        {
            UpdatePointerHover(null);
        }
    }

    public void SetHoldingHand(ETrackedControllerRole? hand)
    {
        if (_window is not IWpfOverlayHoldingHandAware aware)
        {
            return;
        }

        Invoke(() => aware.SetHoldingHand(hand));
        RequestBufferSynchronization();
        Invalidate();
    }

    public void SetInteractionHighlighted(bool highlighted)
    {
        if (_window is not IWpfOverlayInteractionHighlightAware aware)
        {
            return;
        }

        Invoke(() =>
        {
            aware.SetInteractionHighlighted(highlighted);
            if (ResolveVisual() is UIElement visual)
            {
                visual.InvalidateVisual();
            }
        });
        RequestBufferSynchronization();
        Invalidate();
    }

    private void RequestBufferSynchronization() =>
        Interlocked.Exchange(ref _synchronizeBuffersRequested, 1);

    internal NormalizedPoint? FindNamedElementCenter(string elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName))
        {
            return null;
        }

        return Invoke<NormalizedPoint?>(() =>
        {
            var visual = ResolveVisual();
            if (visual is not FrameworkElement root ||
                _window.FindName(elementName) is not FrameworkElement element)
            {
                return null;
            }

            EnsureLayout(root, _fallbackClientWidth, _fallbackClientHeight);
            if (!element.IsVisible ||
                !element.IsEnabled ||
                !element.IsHitTestVisible ||
                element.ActualWidth <= 1 ||
                element.ActualHeight <= 1 ||
                root.ActualWidth <= 1 ||
                root.ActualHeight <= 1)
            {
                return null;
            }

            var center = element.TranslatePoint(
                new Point(element.ActualWidth / 2d, element.ActualHeight / 2d),
                root);
            return new NormalizedPoint(
                (float)(center.X / root.ActualWidth),
                (float)(center.Y / root.ActualHeight)).Clamp();
        });
    }

    internal string? GetHoveredElementNameForDiagnostics() => Invoke(() =>
    {
        var root = ResolveVisual();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is FrameworkElement element && VrPointerHover.GetIsHovered(element))
            {
                return element.Name;
            }
            var children = VisualTreeHelper.GetChildrenCount(current);
            for (var index = children - 1; index >= 0; index--)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
        return null;
    });

    public void Dispose()
    {
        CancelPointer();
        _pixelBuffer = null;
        if (_window is IWpfOverlayInvalidationSource invalidationSource)
        {
            invalidationSource.OverlayInvalidated -= OnOverlayInvalidated;
        }
        if (Options.CloseWindowOnOverlayRemoval)
        {
            _dispatcher.BeginInvoke(() => _window.Close());
        }
    }

    private WpfWindowMetrics ReadWindowMetrics()
    {
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        var requestedWidth = double.IsFinite(_window.Width) && _window.Width > 1
            ? _window.Width
            : 800;
        var requestedHeight = double.IsFinite(_window.Height) && _window.Height > 1
            ? _window.Height
            : 600;
        // 喵~ 用窗口属性值做尺寸快照，避免 Content Actual 被系统工作区压缩后偏离设计值
        // （竖屏 Android 镜像窗口在 200% 缩放/小屏环境中会被压缩，导致 AspectRatio 错误）
        var width = requestedWidth;
        var height = requestedHeight;
        var visual = ResolveVisual();
        // 为后续 HitTest 等操作确保视觉树基础布局可用，但不用该布局的 Actual 尺寸做基准
        EnsureLayout(visual, width, height);
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

    private DirectPointerStart BeginDirectPointer(NormalizedPoint point)
    {
        _directPressedControl = HitTestInteractiveControl(point);
        _directPressedAt = _directPressedControl is null ? null : point;
        if (_directPressedControl is Slider slider)
        {
            SetSliderValue(slider, point);
        }
        return new DirectPointerStart(
            _directPressedControl is not null,
            _directPressedControl?.Name);
    }

    private void UpdateDirectPointer(NormalizedPoint point)
    {
        if (_directPressedControl is Slider slider)
        {
            SetSliderValue(slider, point);
        }
    }

    private bool CompleteDirectPointer(NormalizedPoint point)
    {
        var pressed = _directPressedControl;
        _directPressedControl = null;
        var pressedAt = _directPressedAt;
        _directPressedAt = null;
        if (pressed is Slider slider)
        {
            SetSliderValue(slider, point);
            return true;
        }

        if (pressed is not ButtonBase button ||
            !button.IsEnabled ||
            !button.IsVisible)
        {
            return false;
        }

        var releaseControl = HitTestInteractiveControl(point);
        var pointerStayedCaptured = ReferenceEquals(button, releaseControl) ||
                                    (pressedAt is { } down && PointerDistance(down, point) <= 0.035f);
        if (!pointerStayedCaptured)
        {
            return false;
        }

        switch (button)
        {
            case RadioButton radioButton:
                radioButton.IsChecked = true;
                break;
            case ToggleButton toggleButton:
                toggleButton.IsChecked = toggleButton.IsChecked != true;
                break;
        }
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        if (ResolveVisual() is UIElement visual)
        {
            visual.InvalidateVisual();
        }
        return true;
    }

    private void CancelDirectPointer()
    {
        _directPressedControl = null;
        _directPressedAt = null;
    }

    private static float PointerDistance(NormalizedPoint left, NormalizedPoint right)
    {
        var x = left.X - right.X;
        var y = left.Y - right.Y;
        return MathF.Sqrt((x * x) + (y * y));
    }

    private readonly record struct DirectPointerStart(bool IsActive, string? ControlName);

    private FrameworkElement? HitTestInteractiveControl(NormalizedPoint point)
    {
        var visual = ResolveVisual();
        if (visual is not FrameworkElement root)
        {
            return null;
        }

        EnsureLayout(root, _fallbackClientWidth, _fallbackClientHeight);
        if (_pointerControlResolver?.ResolvePointerControl(point) is
            {
                IsVisible: true,
                IsHitTestVisible: true,
                IsEnabled: true
            } resolved &&
            resolved is ButtonBase or Slider)
        {
            return resolved;
        }

        HitTestResult? hit = null;
        VisualTreeHelper.HitTest(
            root,
            candidate => candidate is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                hit = result;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(
                new Point(point.X * root.ActualWidth, point.Y * root.ActualHeight)));
        for (DependencyObject? current = hit?.VisualHit;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement
                {
                    IsVisible: true,
                    IsHitTestVisible: true,
                    IsEnabled: true
                } interactive &&
                interactive is ButtonBase or Slider)
            {
                return interactive;
            }
            if (ReferenceEquals(current, root))
            {
                break;
            }
        }
        return null;
    }

    private ScrollViewer? HitTestScrollableViewer(NormalizedPoint point)
    {
        var visual = ResolveVisual();
        if (visual is not FrameworkElement root)
        {
            return null;
        }

        EnsureLayout(root, _fallbackClientWidth, _fallbackClientHeight);
        HitTestResult? hit = null;
        VisualTreeHelper.HitTest(
            root,
            candidate => candidate is UIElement { IsVisible: false } or UIElement { IsHitTestVisible: false }
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                hit = result;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(
                new Point(point.X * root.ActualWidth, point.Y * root.ActualHeight)));
        for (DependencyObject? current = hit?.VisualHit;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer
                {
                    IsVisible: true,
                    IsHitTestVisible: true,
                    IsEnabled: true,
                    ScrollableHeight: > 0.01
                } scrollViewer)
            {
                return scrollViewer;
            }
            if (ReferenceEquals(current, root))
            {
                break;
            }
        }
        return null;
    }

    private void SetSliderValue(Slider slider, NormalizedPoint point)
    {
        var visual = ResolveVisual();
        if (visual is not FrameworkElement root || slider.ActualWidth <= 1)
        {
            return;
        }

        var rootPoint = new Point(point.X * root.ActualWidth, point.Y * root.ActualHeight);
        var localPoint = root.TranslatePoint(rootPoint, slider);
        var ratio = Math.Clamp(localPoint.X / slider.ActualWidth, 0d, 1d);
        if (slider.FlowDirection == FlowDirection.RightToLeft)
        {
            ratio = 1d - ratio;
        }
        slider.Value = slider.Minimum + ((slider.Maximum - slider.Minimum) * ratio);
    }

    private void UpdatePointerHover(NormalizedPoint? point) => Invoke(() =>
    {
        if (_pointerHoverAware?.SetPointerHover(point) == true)
        {
            Interlocked.Exchange(ref _synchronizeBuffersRequested, 1);
        }
        if (ResolveVisual() is UIElement visual)
        {
            visual.InvalidateVisual();
            visual.UpdateLayout();
        }
    });

    private static void EnsureLayout(Visual visual, double fallbackWidth, double fallbackHeight)
    {
        if (visual is not FrameworkElement element)
        {
            return;
        }

        var width = double.IsFinite(element.Width) && element.Width > 1
            ? element.Width
            : Math.Max(1, fallbackWidth);
        var height = double.IsFinite(element.Height) && element.Height > 1
            ? element.Height
            : Math.Max(1, fallbackHeight);
        if (!element.IsMeasureValid)
        {
            element.Measure(new Size(width, height));
        }
        if (!element.IsArrangeValid)
        {
            element.Arrange(new Rect(0, 0, width, height));
        }
        if (!element.IsMeasureValid || !element.IsArrangeValid)
        {
            element.UpdateLayout();
        }
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

    private void Invoke(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }
        _dispatcher.Invoke(action);
    }

    private void QueueInvalidateAfterInput() =>
        _dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(Invalidate));

    private void OnOverlayInvalidated(object? sender, EventArgs e) => Invalidate();

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

internal interface IWpfOverlayHoldingHandAware
{
    void SetHoldingHand(ETrackedControllerRole? hand);
}

internal interface IWpfOverlayInteractionHighlightAware
{
    void SetInteractionHighlighted(bool highlighted);
}

internal interface IWpfOverlayInvalidationSource
{
    event EventHandler? OverlayInvalidated;
}

internal interface IWpfOverlayPointerHoverAware
{
    bool SetPointerHover(NormalizedPoint? point);
}

internal interface IWpfOverlayPointerControlResolver
{
    FrameworkElement? ResolvePointerControl(NormalizedPoint point);
}

public static class VrPointerHover
{
    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
        "IsHovered",
        typeof(bool),
        typeof(VrPointerHover),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static bool GetIsHovered(DependencyObject element) =>
        (bool)element.GetValue(IsHoveredProperty);

    public static void SetIsHovered(DependencyObject element, bool value) =>
        element.SetValue(IsHoveredProperty, value);

    public static void SetExclusiveHoveredButton(
        DependencyObject root,
        ButtonBase? hoveredButton)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is ButtonBase button)
            {
                var shouldHover = ReferenceEquals(button, hoveredButton);
                if (GetIsHovered(button) != shouldHover)
                {
                    SetIsHovered(button, shouldHover);
                }
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = childCount - 1; index >= 0; index--)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }
}

internal interface IWpfOverlayPointerSink
{
    void PointerMove(NormalizedPoint point);

    string? PointerDown(NormalizedPoint point);

    bool? PointerUp(NormalizedPoint point);

    void PointerCancel();
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

internal sealed record ResizeWpfOverlayCommand(
    long OverlayId,
    float WidthMeters) : WpfOverlayCommand;

internal readonly record struct DiagnosticWpfPointerCommand(
    long RequestId,
    long OverlayId,
    NormalizedPoint TexturePoint,
    bool IsPressed,
    bool DispatchInput,
    bool UseOpenVrIntersection,
    bool Leave,
    long RequestedTimestamp);

internal enum DiagnosticWpfInteractionKind
{
    Click,
    Scroll
}

internal readonly record struct DiagnosticWpfInteractionCommand(
    long RequestId,
    long OverlayId,
    DiagnosticWpfInteractionKind Kind,
    string? ControlName,
    NormalizedPoint TexturePoint,
    int WheelDelta,
    long RequestedTimestamp);

internal readonly record struct PendingDiagnosticWpfInteraction(
    long RequestId,
    long OverlayId,
    DiagnosticWpfInteractionKind Kind,
    string? ControlName,
    long RequestedTimestamp,
    long ProcessedTimestamp);
