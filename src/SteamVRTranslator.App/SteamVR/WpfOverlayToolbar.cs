using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

namespace SteamVRTranslator.App.SteamVR;

internal sealed class WpfOverlayToolbar
{
    private static Brush ToolbarBackground =>
        FrozenBrush(OverlayTheme.Resolve(OverlayColorRole.Surface, Color.FromArgb(210, 52, 56, 59)));
    private static Brush ToolbarBorder =>
        FrozenBrush(OverlayTheme.Resolve(OverlayColorRole.Border, Color.FromArgb(220, 235, 238, 240)));
    private static Brush HoverBackground =>
        FrozenBrush(OverlayTheme.Resolve(OverlayColorRole.AccentSoft, Color.FromArgb(235, 86, 92, 97)));
    private static Brush PressedBackground =>
        FrozenBrush(OverlayTheme.Resolve(OverlayColorRole.AccentHover, Color.FromArgb(245, 112, 118, 123)));
    private static Brush RecordingForeground =>
        FrozenBrush(OverlayTheme.Resolve(OverlayColorRole.Danger, Color.FromRgb(239, 68, 68)));

    private readonly Border _root;
    private readonly IReadOnlyList<OverlayToolbarAction> _actions;
    private readonly Dictionary<OverlayToolbarAction, Border> _buttons = [];
    private readonly Dictionary<OverlayToolbarAction, Viewbox> _iconHosts = [];
    private readonly Dictionary<OverlayToolbarAction, ShapePath> _icons = [];
    private ToolbarRenderKey? _lastRenderKey;
    private BitmapSource? _lastRenderedBitmap;

    public WpfOverlayToolbar(
        InteractiveOverlayKind kind,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
        : this(OverlayToolbarLayout.PrimaryActions(kind, side))
    {
    }

    public WpfOverlayToolbar(IReadOnlyList<OverlayToolbarAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = actions;
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(OverlayToolbarLayout.ContentPadding),
            Background = Brushes.Transparent
        };
        for (var index = 0; index < _actions.Count; index++)
        {
            var action = _actions[index];
            var button = CreateButton(action);
            button.Margin = new Thickness(
                0,
                0,
                index == _actions.Count - 1 ? 0 : OverlayToolbarLayout.ButtonGap,
                0);
            buttonPanel.Children.Add(button);
            _buttons.Add(action, button);
        }

        _root = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ClipToBounds = false,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = buttonPanel
        };
    }

    public BitmapSource Render(
        int pixelWidth,
        int pixelHeight,
        OverlayToolbarAction? hoveredAction,
        bool pointerPressed,
        int renderScale = 1,
        bool isCommandRecording = false)
    {
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        renderScale = Math.Max(1, renderScale);
        pointerPressed = hoveredAction is not null && pointerPressed;
        var renderKey = new ToolbarRenderKey(
            pixelWidth,
            pixelHeight,
            hoveredAction,
            pointerPressed,
            renderScale,
            isCommandRecording,
            OverlayTheme.Instance.Revision);
        if (_lastRenderKey == renderKey && _lastRenderedBitmap is not null)
        {
            return _lastRenderedBitmap;
        }

        var buttonSize = Math.Max(1, pixelHeight - (OverlayToolbarLayout.ContentPadding * 2));
        var iconSize = Math.Clamp(pixelHeight * 0.46, 9, 18);
        foreach (var (action, button) in _buttons)
        {
            button.Width = buttonSize;
            button.Height = buttonSize;
            button.BorderBrush = ToolbarBorder;
            _iconHosts[action].Width = iconSize;
            _iconHosts[action].Height = iconSize;
            _icons[action].Fill = action == OverlayToolbarAction.CustomCommand && isCommandRecording
                ? RecordingForeground
                : OverlayTheme.Brush(action == hoveredAction && pointerPressed
                    ? OverlayColorRole.OnAccent : OverlayColorRole.Text, "#FFFFFF");
            button.Background = action == hoveredAction
                ? pointerPressed ? PressedBackground : HoverBackground
                : ToolbarBackground;
        }

        _root.Width = pixelWidth;
        _root.Height = pixelHeight;
        _root.Measure(new Size(pixelWidth, pixelHeight));
        _root.Arrange(new Rect(0, 0, pixelWidth, pixelHeight));
        _root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            checked(pixelWidth * renderScale),
            checked(pixelHeight * renderScale),
            96d * renderScale,
            96d * renderScale,
            PixelFormats.Pbgra32);
        bitmap.Render(_root);
        bitmap.Freeze();
        _lastRenderKey = renderKey;
        _lastRenderedBitmap = bitmap;
        return bitmap;
    }

    internal static Geometry IconGeometry(OverlayToolbarAction action) => action switch
    {
        OverlayToolbarAction.Translate => MaterialIconPaths.Translate,
        OverlayToolbarAction.LayoutTranslate => MaterialIconPaths.ViewDashboardOutline,
        OverlayToolbarAction.CustomCommand => MaterialIconPaths.Microphone,
        OverlayToolbarAction.Close => MaterialIconPaths.Close,
        OverlayToolbarAction.Send => MaterialIconPaths.Send,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    internal static Viewbox CreateIconView(OverlayToolbarAction action, out ShapePath icon)
    {
        icon = new ShapePath
        {
            Data = IconGeometry(action),
            Fill = OverlayTheme.Brush(OverlayColorRole.Text, "#FFFFFF"),
            Width = 24,
            Height = 24,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true
        };
        var designCanvas = new Canvas
        {
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent
        };
        designCanvas.Children.Add(icon);
        return new Viewbox
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = designCanvas
        };
    }

    private Border CreateButton(OverlayToolbarAction action)
    {
        var iconHost = CreateIconView(action, out var icon);
        _iconHosts.Add(action, iconHost);
        _icons.Add(action, icon);
        return new Border
        {
            Child = iconHost,
            Background = ToolbarBackground,
            BorderBrush = ToolbarBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            SnapsToDevicePixels = true
        };
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly record struct ToolbarRenderKey(
        int PixelWidth,
        int PixelHeight,
        OverlayToolbarAction? HoveredAction,
        bool PointerPressed,
        int RenderScale,
        bool IsCommandRecording,
        long ThemeRevision);
}
