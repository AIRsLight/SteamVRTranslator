using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.App.AndroidMirror;

internal partial class AndroidMirrorWindow : Window,
    IWpfOverlayInteractionHighlightAware,
    IWpfOverlayInvalidationSource,
    IWpfOverlayDirectPixelSource,
    IWpfOverlayPointerSink,
    IWpfOverlayPointerHoverAware
{
    private const double LogicalLongEdge = 900d;
    private const double NavigationHeight = 50d;
    private const double FrameThickness = 4d;
    private readonly ScrcpyAndroidSession _session;
    private TaskCompletionSource<AndroidVideoFrame>? _firstFrame = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private AndroidVideoFrame? _latestFrame;
    private DirectOverlayPixelRegion _screenRegion = DirectOverlayPixelRegion.Full;
    private DirectOverlayPixelRegion _directPixelRegion = DirectOverlayPixelRegion.Full;
    private double _windowAspectRatio = 400d / 920d;
    private int _hasVideoFrame;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _touchActive;
    private readonly AndroidTouchStabilizer _touchStabilizer = new();
    private AndroidNavigationKey? _pressedNavigationKey;
    private ButtonBase? _hoveredNavigationButton;
    private long _lastMoveTimestamp;

    internal AndroidMirrorWindow(ScrcpyAndroidSession session)
    {
        _session = session;
        InitializeComponent();
        _session.FrameReceived += Session_FrameReceived;
        _session.StatusChanged += Session_StatusChanged;
        Closed += OnClosed;
    }

    public event EventHandler? OverlayInvalidated;

    public long LatestDirectPixelSequence =>
        Volatile.Read(ref _latestFrame)?.Sequence ?? long.MinValue;

    public DirectOverlayPixelRegion DirectPixelRegion =>
        Volatile.Read(ref _directPixelRegion);

    internal double RecommendedWidthMeters { get; private set; } =
        AndroidMirrorConfiguration.DefaultWindowWidthMeters;

    internal double RecommendedHeightMeters =>
        RecommendedWidthMeters * Height / Math.Max(1d, Width);

    internal async Task<AndroidVideoFrame> WaitForFirstFrameAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completion = Volatile.Read(ref _firstFrame);
        if (completion is null)
        {
            throw new InvalidOperationException("Android mirror first frame is no longer available.");
        }

        var frame = await completion.Task.WaitAsync(timeout, cancellationToken);
        Interlocked.CompareExchange(ref _firstFrame, null, completion);
        return frame;
    }

    internal void ConfigureForSource(
        int sourceWidth,
        int sourceHeight,
        double portraitWidthMeters = AndroidMirrorConfiguration.DefaultWindowWidthMeters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ConfigureForSource(
                sourceWidth,
                sourceHeight,
                portraitWidthMeters));
            return;
        }

        var scale = LogicalLongEdge / Math.Max(sourceWidth, sourceHeight);
        var screenWidth = Math.Max(300d, Math.Round(sourceWidth * scale));
        var screenHeight = Math.Max(300d, Math.Round(sourceHeight * scale));
        PhoneImage.Width = screenWidth;
        PhoneImage.Height = screenHeight;
        ScreenRow.Height = new GridLength(screenHeight);
        NavigationRow.Height = new GridLength(NavigationHeight);
        Width = screenWidth + FrameThickness;
        Height = screenHeight + NavigationHeight + FrameThickness;
        var screenRegion = new DirectOverlayPixelRegion(
            (float)((FrameThickness / 2d) / Width),
            (float)((FrameThickness / 2d) / Height),
            (float)(screenWidth / Width),
            (float)(screenHeight / Height));
        Volatile.Write(ref _screenRegion, screenRegion);
        Volatile.Write(ref _windowAspectRatio, Width / Height);
        UpdateVideoRegion(sourceWidth, sourceHeight);

        var normalizedPortraitWidth = AndroidMirrorConfiguration.NormalizeWindowWidthMeters(
            portraitWidthMeters);
        var normalizedLongSide = normalizedPortraitWidth /
                                 AndroidMirrorConfiguration.DefaultWindowWidthMeters *
                                 AndroidMirrorConfiguration.DefaultWindowLongSideMeters;
        var windowAspectRatio = Width / Height;
        RecommendedWidthMeters = sourceHeight >= sourceWidth
            ? normalizedLongSide * windowAspectRatio
            : normalizedLongSide;
        Volatile.Write(ref _sourceWidth, sourceWidth);
        Volatile.Write(ref _sourceHeight, sourceHeight);
        StatusPanel.Visibility = Visibility.Collapsed;
        InvalidateVisual();
        UpdateLayout();
    }

    internal void ApplyPhysicalScale(double portraitWidthMeters)
    {
        var sourceWidth = Volatile.Read(ref _sourceWidth);
        var sourceHeight = Volatile.Read(ref _sourceHeight);
        if (sourceWidth > 0 && sourceHeight > 0)
        {
            ConfigureForSource(sourceWidth, sourceHeight, portraitWidthMeters);
        }
    }

    public void SetInteractionHighlighted(bool highlighted)
    {
        WindowFrame.BorderBrush = highlighted
            ? new SolidColorBrush(Color.FromRgb(46, 229, 140))
            : Brushes.Transparent;
        InvalidateOverlay();
    }

    public bool SetPointerHover(NormalizedPoint? point)
    {
        var hovered = point is { } normalized
            ? FindNavigationButton(normalized)?.Button
            : null;
        if (ReferenceEquals(_hoveredNavigationButton, hovered))
        {
            if (hovered is not null && !VrPointerHover.GetIsHovered(hovered))
            {
                VrPointerHover.SetExclusiveHoveredButton(WindowFrame, hovered);
                InvalidateOverlay();
                return true;
            }
            return false;
        }

        _hoveredNavigationButton = hovered;
        VrPointerHover.SetExclusiveHoveredButton(WindowFrame, _hoveredNavigationButton);
        InvalidateOverlay();
        return true;
    }

    public void PointerMove(NormalizedPoint point)
    {
        if (!_touchActive || !TryMapToPhone(point, out var touch))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastMoveTimestamp) < 8)
        {
            return;
        }

        Interlocked.Exchange(ref _lastMoveTimestamp, now);
        if (_touchStabilizer.TryMove(touch, out var move))
        {
            _session.QueueTouch(move);
        }
    }

    public string? PointerDown(NormalizedPoint point)
    {
        if (FindNavigationButton(point) is { } navigation)
        {
            _pressedNavigationKey = navigation.Key;
            return navigation.Name;
        }
        if (!TryMapToPhone(point, out var touch))
        {
            return null;
        }

        _touchActive = true;
        _session.QueueTouch(_touchStabilizer.Begin(touch));
        return "AndroidScreen";
    }

    public bool? PointerUp(NormalizedPoint point)
    {
        if (_pressedNavigationKey is { } key)
        {
            _pressedNavigationKey = null;
            var released = FindNavigationButton(point);
            if (released?.Key == key)
            {
                return _session.QueueNavigationKey(key);
            }
            return false;
        }
        if (!_touchActive)
        {
            return false;
        }

        _touchActive = false;
        var hasReleasePoint = TryMapToPhone(point, out var touch);
        _session.QueueTouch(_touchStabilizer.Complete(hasReleasePoint ? touch : null));
        return true;
    }

    public void PointerCancel()
    {
        _pressedNavigationKey = null;
        if (!_touchActive)
        {
            return;
        }

        _touchActive = false;
        if (_touchStabilizer.Cancel() is { } cancel)
        {
            _session.QueueTouch(cancel);
        }
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase { Tag: string value } &&
            Enum.TryParse<AndroidNavigationKey>(value, out var key))
        {
            _session.QueueNavigationKey(key);
        }
    }

    private void Session_FrameReceived(object? sender, AndroidVideoFrame frame)
    {
        Volatile.Read(ref _firstFrame)?.TrySetResult(frame);
        Volatile.Write(ref _sourceWidth, frame.Width);
        Volatile.Write(ref _sourceHeight, frame.Height);
        UpdateVideoRegion(frame.Width, frame.Height);
        Interlocked.Exchange(ref _latestFrame, frame);
        Volatile.Write(ref _hasVideoFrame, 1);
    }

    public bool TryGetLatestDirectPixelFrame(out DirectOverlayPixelFrame frame)
    {
        if (Volatile.Read(ref _latestFrame) is not { } latest)
        {
            frame = default;
            return false;
        }

        frame = new DirectOverlayPixelFrame(
            latest.BgraPixels,
            latest.Width,
            latest.Height,
            latest.Sequence,
            DirectOverlayPixelFormat.Bgra);
        return true;
    }

    private void Session_StatusChanged(object? sender, string message)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (Volatile.Read(ref _hasVideoFrame) == 0)
            {
                StatusText.Text = message;
                StatusPanel.Visibility = Visibility.Visible;
                InvalidateOverlay();
            }
        });
    }

    private NavigationButtonHit? FindNavigationButton(NormalizedPoint point)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => FindNavigationButton(point));
        }
        if (RootGrid.ActualWidth <= 1 || RootGrid.ActualHeight <= 1)
        {
            return null;
        }

        var rootX = Math.Clamp(point.X, 0f, 1f) * RootGrid.ActualWidth;
        var rootY = Math.Clamp(point.Y, 0f, 1f) * RootGrid.ActualHeight;
        var navigationHeight = NavigationRow.ActualHeight > 1
            ? NavigationRow.ActualHeight
            : NavigationHeight;
        if (rootY < RootGrid.ActualHeight - navigationHeight)
        {
            return null;
        }

        NavigationButtonHit[] candidates =
        [
            new(BackButton, nameof(BackButton), AndroidNavigationKey.Back),
            new(HomeButton, nameof(HomeButton), AndroidNavigationKey.Home),
            new(RecentAppsButton, nameof(RecentAppsButton), AndroidNavigationKey.RecentApps)
        ];
        return candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Center = candidate.Button.TranslatePoint(
                    new Point(candidate.Button.ActualWidth / 2d, candidate.Button.ActualHeight / 2d),
                    RootGrid).X
            })
            .MinBy(candidate => Math.Abs(candidate.Center - rootX))
            ?.Candidate;
    }

    private bool TryMapToPhone(NormalizedPoint point, out AndroidTouchEvent touch)
    {
        var width = Volatile.Read(ref _sourceWidth);
        var height = Volatile.Read(ref _sourceHeight);
        return AndroidTouchCoordinateMapper.TryMap(
            point,
            Volatile.Read(ref _directPixelRegion),
            width,
            height,
            out touch);
    }

    private void UpdateVideoRegion(int sourceWidth, int sourceHeight)
    {
        var fitted = AndroidTouchCoordinateMapper.FitVideoRegion(
            Volatile.Read(ref _screenRegion),
            Volatile.Read(ref _windowAspectRatio),
            sourceWidth,
            sourceHeight);
        Volatile.Write(ref _directPixelRegion, fitted);
    }

    private void InvalidateOverlay() => OverlayInvalidated?.Invoke(this, EventArgs.Empty);

    private void OnClosed(object? sender, EventArgs e)
    {
        PointerCancel();
        SetPointerHover(null);
        Interlocked.Exchange(ref _firstFrame, null)?.TrySetCanceled();
        _session.FrameReceived -= Session_FrameReceived;
        _session.StatusChanged -= Session_StatusChanged;
        Closed -= OnClosed;
    }

    private sealed record NavigationButtonHit(
        ButtonBase Button,
        string Name,
        AndroidNavigationKey Key);
}

internal static class AndroidTouchCoordinateMapper
{
    private const float BoundaryEpsilon = 0.0001f;

    public static DirectOverlayPixelRegion FitVideoRegion(
        DirectOverlayPixelRegion screenRegion,
        double windowAspectRatio,
        int sourceWidth,
        int sourceHeight)
    {
        var region = screenRegion.Clamp();
        if (!double.IsFinite(windowAspectRatio) ||
            windowAspectRatio <= 0 ||
            sourceWidth <= 0 ||
            sourceHeight <= 0)
        {
            return region;
        }

        var sourceAspectRatio = (double)sourceWidth / sourceHeight;
        var screenAspectRatio = windowAspectRatio * region.Width / region.Height;
        if (Math.Abs(sourceAspectRatio - screenAspectRatio) < 0.0001d)
        {
            return region;
        }

        if (screenAspectRatio > sourceAspectRatio)
        {
            var fittedWidth = (float)(region.Height * sourceAspectRatio / windowAspectRatio);
            return new DirectOverlayPixelRegion(
                region.X + ((region.Width - fittedWidth) / 2f),
                region.Y,
                fittedWidth,
                region.Height).Clamp();
        }

        var fittedHeight = (float)(region.Width * windowAspectRatio / sourceAspectRatio);
        return new DirectOverlayPixelRegion(
            region.X,
            region.Y + ((region.Height - fittedHeight) / 2f),
            region.Width,
            fittedHeight).Clamp();
    }

    public static bool TryMap(
        NormalizedPoint point,
        DirectOverlayPixelRegion videoRegion,
        int sourceWidth,
        int sourceHeight,
        out AndroidTouchEvent touch)
    {
        touch = default!;
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return false;
        }

        var region = videoRegion.Clamp();
        var right = region.X + region.Width;
        var bottom = region.Y + region.Height;
        if (point.X < region.X - BoundaryEpsilon || point.X > right + BoundaryEpsilon ||
            point.Y < region.Y - BoundaryEpsilon || point.Y > bottom + BoundaryEpsilon)
        {
            return false;
        }

        var normalizedX = Math.Clamp((point.X - region.X) / region.Width, 0f, 1f);
        var normalizedY = Math.Clamp((point.Y - region.Y) / region.Height, 0f, 1f);
        touch = new AndroidTouchEvent(
            AndroidTouchAction.Move,
            Math.Clamp((int)Math.Round(normalizedX * (sourceWidth - 1)), 0, sourceWidth - 1),
            Math.Clamp((int)Math.Round(normalizedY * (sourceHeight - 1)), 0, sourceHeight - 1),
            sourceWidth,
            sourceHeight);
        return true;
    }
}

internal sealed class AndroidTouchStabilizer
{
    private const double TapJitterRatio = 24d / 1080d;
    private const double MinimumTapJitterPixels = 16d;
    private AndroidTouchEvent? _pressedAt;
    private AndroidTouchEvent? _lastSent;
    private bool _dragging;

    public AndroidTouchEvent Begin(AndroidTouchEvent touch)
    {
        var down = touch with { Action = AndroidTouchAction.Down };
        _pressedAt = down;
        _lastSent = down;
        _dragging = false;
        return down;
    }

    public bool TryMove(AndroidTouchEvent touch, out AndroidTouchEvent move)
    {
        move = default!;
        if (_pressedAt is not { } pressed)
        {
            return false;
        }

        if (!_dragging)
        {
            var deltaX = touch.X - pressed.X;
            var deltaY = touch.Y - pressed.Y;
            var shortEdge = Math.Min(touch.ScreenWidth, touch.ScreenHeight);
            var threshold = Math.Max(MinimumTapJitterPixels, shortEdge * TapJitterRatio);
            if ((deltaX * deltaX) + (deltaY * deltaY) < threshold * threshold)
            {
                return false;
            }
            _dragging = true;
        }

        move = touch with { Action = AndroidTouchAction.Move };
        _lastSent = move;
        return true;
    }

    public AndroidTouchEvent Complete(AndroidTouchEvent? release)
    {
        var pressed = _pressedAt ?? release ?? new AndroidTouchEvent(
            AndroidTouchAction.Down,
            0,
            0,
            1,
            1);
        var final = _dragging
            ? release ?? _lastSent ?? pressed
            : pressed;
        Reset();
        return final with { Action = AndroidTouchAction.Up };
    }

    public AndroidTouchEvent? Cancel()
    {
        var final = _lastSent ?? _pressedAt;
        Reset();
        return final is null ? null : final with { Action = AndroidTouchAction.Cancel };
    }

    private void Reset()
    {
        _pressedAt = null;
        _lastSent = null;
        _dragging = false;
    }
}
