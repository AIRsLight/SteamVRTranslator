using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.App.SteamVR;

internal sealed class OverlayRenderer
{
    private static readonly byte[] UnpremultiplyLookup = CreateUnpremultiplyLookup();
    private const double ResultCornerRadius = 9;
    private const double ResultContentOpacity = 0.96;
    // Interactive layouts use a 512-DIP long edge and select a matching pixel tier.
    public const int Width = 512;
    public const int Height = 512;
    public const int SmallTextureLongEdge = 512;
    public const int DefaultTextureLongEdge = 1024;
    public const int HtmlTextureLongEdge = 2048;
    public const int ChromeTextureLongEdge = 1024;
    public const int PointerTextureSize = 64;
    public const int PointerRayTextureWidth = 256;
    public const int PointerRayTextureHeight = 16;
    public const int ProgressTextureSize = 256;
    public const double PointerLogicalExtent = 32;
    public const float PointerRayThicknessMeters = 0.0035f;
    public const int TextureScale = 2;
    public const int TextureWidth = DefaultTextureLongEdge;
    public const int TextureHeight = DefaultTextureLongEdge;
    public const int HtmlTextureScale = 4;
    public const int HtmlTextureWidth = HtmlTextureLongEdge;
    public const int HtmlTextureHeight = HtmlTextureLongEdge;

    private readonly Typeface _typeface = new("Microsoft YaHei UI");
    private readonly Dictionary<(InteractiveOverlayKind Kind, OverlayToolbarSide Side), WpfOverlayToolbar> _toolbars = [];
    private readonly Dictionary<(InteractiveOverlayKind Kind, OverlayToolbarSide Side), WpfOverlayToolbar> _voiceButtons = [];
    private readonly ConditionalWeakTable<byte[], BitmapSource> _decodedImages = new();
    private readonly ConditionalWeakTable<object, ResultControlSurfaceCache> _resultControls = new();
    private readonly Dictionary<bool, OverlayRenderFrame> _pointerFrames = [];
    private OverlayRenderFrame? _pointerRayFrame;
    private static readonly Lazy<Dispatcher> FallbackDispatcher = new(CreateFallbackDispatcher);

    public byte[] RenderSelection(
        SelectionSnapshot snapshot,
        bool frameUsable = true,
        string? invalidHint = null) =>
        OnUiThread(() => RenderSelectionCore(snapshot, frameUsable, invalidHint));

    public ResultRenderFrame RenderResults(
        ResultOverlaySnapshot snapshot,
        SpatialSelectionPlane plane,
        bool highlighted = false,
        bool showToolbar = false,
        OverlayPointerVisual? pointer = null,
        bool isCommandRecording = false,
        double? closeHoldProgress = null,
        OverlayToolbarSide toolbarSide = OverlayToolbarSide.Right,
        object? cacheOwner = null) =>
        OnUiThread(() => RenderResultsCore(
            snapshot,
            plane,
            highlighted,
            showToolbar,
            pointer,
            isCommandRecording,
            closeHoldProgress,
            toolbarSide,
            cacheOwner));

    public OverlayRenderFrame RenderCapture(
        byte[] jpeg,
        SpatialSelectionPlane plane,
        bool highlighted = false,
        bool showToolbar = false,
        OverlayPointerVisual? pointer = null,
        bool isCommandRecording = false,
        double? closeHoldProgress = null,
        OverlayToolbarSide toolbarSide = OverlayToolbarSide.Right) =>
        OnUiThread(() => RenderCaptureCore(
            jpeg,
            plane,
            highlighted,
            showToolbar,
            pointer,
            isCommandRecording,
            closeHoldProgress,
            toolbarSide));

    public OverlayRenderFrame RenderWindow(
        WpfWindowOverlaySource source,
        SpatialSelectionPlane plane) =>
        RenderWindowCore(source, plane);

    public OverlayRenderFrame RenderChrome(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        bool showToolbar,
        OverlayPointerVisual? pointer,
        bool isCommandRecording,
        OverlayToolbarSide toolbarSide = OverlayToolbarSide.Right) =>
        OnUiThread(() => RenderChromeCore(
            kind,
            plane,
            showToolbar,
            pointer,
            isCommandRecording,
            toolbarSide));

    public OverlayRenderFrame RenderCloseHoldProgress(
        SpatialSelectionPlane plane,
        double progress) =>
        OnUiThread(() => RenderCloseHoldProgressCore(plane, progress));

    public OverlayRenderFrame RenderPointer(bool isPressed) =>
        OnUiThread(() =>
        {
            if (_pointerFrames.TryGetValue(isPressed, out var cached))
            {
                return cached;
            }

            var rendered = RenderPointerCore(isPressed);
            _pointerFrames.Add(isPressed, rendered);
            return rendered;
        });

    public OverlayRenderFrame RenderPointerRay() =>
        OnUiThread(() => _pointerRayFrame ??= RenderPointerRayCore());

    public string ExtractVisibleText(ResultContentFormat format, string text) =>
        format switch
        {
            ResultContentFormat.Markdown =>
                OnUiThread(() => ResultContentFormatter.ExtractMarkdownVisibleText(text)),
            ResultContentFormat.Html => ResultContentFormatter.ExtractHtmlVisibleText(text),
            _ => ResultContentFormatter.NormalizeVisibleText(text)
        };

    private byte[] RenderSelectionCore(
        SelectionSnapshot snapshot,
        bool frameUsable,
        string? invalidHint)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var accent = !frameUsable && snapshot.Region is not null
                ? Color.FromRgb(239, 68, 68)
                : Color.FromRgb(46, 229, 140);
            if (snapshot.Region is { } region)
            {
                var rectangle = new Rect(
                    region.Left * Width,
                    region.Top * Height,
                    region.Width * Width,
                    region.Height * Height);
                DrawSelectionGrid(drawing, rectangle, accent);
                DrawCornerHandles(drawing, rectangle, accent);
                DrawSelectionHint(
                    drawing,
                    rectangle,
                    SelectionHint(snapshot.State, frameUsable, invalidHint),
                    accent);
            }
            else if (snapshot.State == SelectionState.Armed)
            {
                DrawArmedHint(drawing);
            }
        }

        return RenderVisual(visual, OverlayRenderSize.Square(DefaultTextureLongEdge)).Pixels;
    }

    private ResultRenderFrame RenderResultsCore(
        ResultOverlaySnapshot snapshot,
        SpatialSelectionPlane plane,
        bool highlighted,
        bool showToolbar,
        OverlayPointerVisual? pointer,
        bool isCommandRecording,
        double? closeHoldProgress,
        OverlayToolbarSide toolbarSide,
        object? cacheOwner)
    {
        var textureLongEdge = snapshot is
        {
            ContentFormat: ResultContentFormat.Html,
            RenderedImage.Length: > 0
        }
            ? HtmlTextureLongEdge
            : DefaultTextureLongEdge;
        var renderSize = CalculateRenderSize(plane, textureLongEdge);
        if (!highlighted &&
            !showToolbar &&
            pointer is null &&
            !isCommandRecording &&
            closeHoldProgress is null &&
            snapshot is not
            {
                ContentFormat: ResultContentFormat.Html,
                RenderedImage.Length: > 0
            })
        {
            var control = GetOrRenderResultControl(
                snapshot,
                renderSize.LogicalSize,
                renderSize.RenderScale,
                cacheOwner);
            return new ResultRenderFrame(
                CopyBitmapPixels(control.Bitmap, ResultContentOpacity),
                control.MaximumScrollOffset,
                control.Bitmap.PixelWidth,
                control.Bitmap.PixelHeight);
        }

        var visual = new DrawingVisual();
        double maximumScroll;
        using (var drawing = visual.RenderOpen())
        {
            maximumScroll = DrawResultPanel(
                drawing,
                snapshot,
                plane,
                highlighted,
                renderSize.RenderScale,
                cacheOwner);
            if (showToolbar || isCommandRecording)
            {
                DrawToolbar(
                    drawing,
                    InteractiveOverlayKind.Result,
                    plane,
                    pointer,
                    renderSize.RenderScale,
                    showToolbar,
                    isCommandRecording,
                    toolbarSide);
            }
            DrawCloseHoldProgress(drawing, plane, closeHoldProgress);
            DrawPointer(drawing, pointer, renderSize.LogicalSize);
        }

        return new ResultRenderFrame(
            RenderVisual(visual, renderSize).Pixels,
            maximumScroll,
            renderSize.PixelWidth,
            renderSize.PixelHeight);
    }

    private OverlayRenderFrame RenderCaptureCore(
        byte[] jpeg,
        SpatialSelectionPlane plane,
        bool highlighted,
        bool showToolbar,
        OverlayPointerVisual? pointer,
        bool isCommandRecording,
        double? closeHoldProgress,
        OverlayToolbarSide toolbarSide)
    {
        var bitmap = DecodeBitmap(jpeg);
        var textureLongEdge = SelectTextureLongEdge(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            DefaultTextureLongEdge);
        var renderSize = CalculateRenderSize(plane, textureLongEdge);

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var panel = CalculateResultPanel(plane);
            drawing.DrawImage(bitmap, panel);
            if (InteractionPen(highlighted) is { } interactionPen)
            {
                drawing.DrawRectangle(null, interactionPen, panel);
            }
            if (showToolbar || isCommandRecording)
            {
                DrawToolbar(
                    drawing,
                    InteractiveOverlayKind.Capture,
                    plane,
                    pointer,
                    renderSize.RenderScale,
                    showToolbar,
                    isCommandRecording,
                    toolbarSide);
            }
            DrawCloseHoldProgress(drawing, plane, closeHoldProgress);
            DrawPointer(drawing, pointer, renderSize.LogicalSize);
        }

        return RenderVisual(visual, renderSize);
    }

    private OverlayRenderFrame RenderWindowCore(
        WpfWindowOverlaySource source,
        SpatialSelectionPlane plane)
    {
        var textureLongEdge = SelectTextureLongEdge(
            source.SourcePixelWidth,
            source.SourcePixelHeight,
            DefaultTextureLongEdge);
        var renderSize = CalculateRenderSize(plane, textureLongEdge);
        var bitmap = source.RenderPixels(renderSize.PixelWidth, renderSize.PixelHeight);
        var pixels = source.GetReusablePixelBuffer(
            checked(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        ConvertPbgraToRgbaInPlace(pixels);
        return new OverlayRenderFrame(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private OverlayRenderFrame RenderChromeCore(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        bool showToolbar,
        OverlayPointerVisual? pointer,
        bool isCommandRecording,
        OverlayToolbarSide toolbarSide)
    {
        var renderSize = CalculateRenderSize(plane, ChromeTextureLongEdge);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (showToolbar || isCommandRecording)
            {
                DrawToolbar(
                    drawing,
                    kind,
                    plane,
                    pointer,
                    renderSize.RenderScale,
                    showToolbar,
                    isCommandRecording,
                    toolbarSide);
            }
        }

        return RenderVisual(visual, renderSize);
    }

    private static OverlayRenderFrame RenderCloseHoldProgressCore(
        SpatialSelectionPlane plane,
        double progress)
    {
        const double logicalSize = 64;
        const double indicatorSize = 56;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            DrawCloseHoldProgressAt(
                drawing,
                new Point(logicalSize / 2, logicalSize / 2),
                indicatorSize,
                progress);
        }

        return RenderVisual(
            visual,
            new OverlayRenderSize(
                new Size(logicalSize, logicalSize),
                ProgressTextureSize,
                ProgressTextureSize,
                ProgressTextureSize / (int)logicalSize));
    }

    private static OverlayRenderFrame RenderPointerCore(bool isPressed)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            DrawPointer(
                drawing,
                new OverlayPointerVisual(
                    Valve.VR.ETrackedControllerRole.RightHand,
                    new NormalizedPoint(0.5f, 0.5f),
                    new NormalizedPoint(0.5f, 0.5f),
                    isPressed),
                new Size(PointerLogicalExtent, PointerLogicalExtent));
        }

        return RenderVisual(
            visual,
            new OverlayRenderSize(
                new Size(PointerLogicalExtent, PointerLogicalExtent),
                PointerTextureSize,
                PointerTextureSize,
                PointerTextureSize / (int)PointerLogicalExtent));
    }

    private static OverlayRenderFrame RenderPointerRayCore()
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var line = new Pen(
                new SolidColorBrush(Color.FromRgb(77, 224, 193)),
                2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            drawing.DrawLine(
                line,
                new Point(4, PointerRayTextureHeight / 2d),
                new Point(PointerRayTextureWidth - 4, PointerRayTextureHeight / 2d));
        }

        return RenderVisual(
            visual,
            new OverlayRenderSize(
                new Size(PointerRayTextureWidth, PointerRayTextureHeight),
                PointerRayTextureWidth,
                PointerRayTextureHeight,
                1));
    }

    private double DrawResultPanel(
        DrawingContext drawing,
        ResultOverlaySnapshot result,
        SpatialSelectionPlane plane,
        bool highlighted,
        int renderScale,
        object? cacheOwner)
    {
        var panel = CalculateResultPanel(plane);
        if (result is
            {
                ContentFormat: ResultContentFormat.Html,
                RenderedImage.Length: > 0
            })
        {
            try
            {
                var bitmap = DecodeBitmap(result.RenderedImage);
                drawing.PushOpacity(ResultContentOpacity);
                drawing.DrawImage(bitmap, panel);
                drawing.Pop();
                if (InteractionPen(highlighted) is { } htmlInteractionPen)
                {
                    drawing.DrawRoundedRectangle(
                        null,
                        htmlInteractionPen,
                        panel,
                        ResultCornerRadius,
                        ResultCornerRadius);
                }
                return 0;
            }
            catch
            {
                result = result with
                {
                    Text = result.VisibleText,
                    ContentFormat = ResultContentFormat.PlainText,
                    RenderedImage = null
                };
            }
        }

        var rendered = GetOrRenderResultControl(result, panel.Size, renderScale, cacheOwner);
        drawing.PushOpacity(ResultContentOpacity);
        drawing.DrawImage(rendered.Bitmap, panel);
        drawing.Pop();
        if (InteractionPen(highlighted) is { } interactionPen)
        {
            drawing.DrawRoundedRectangle(
                null,
                interactionPen,
                panel,
                ResultCornerRadius,
                ResultCornerRadius);
        }
        return rendered.MaximumScrollOffset;
    }

    private ResultControlFrame GetOrRenderResultControl(
        ResultOverlaySnapshot result,
        Size size,
        int renderScale,
        object? cacheOwner)
    {
        var key = new ResultControlFrameCacheKey(
            Math.Max(8, (int)Math.Round(size.Width * renderScale)),
            Math.Max(8, (int)Math.Round(size.Height * renderScale)),
            Math.Max(1, renderScale));
        var cache = _resultControls.GetValue(
            cacheOwner ?? result,
            static _ => new ResultControlSurfaceCache());
        return cache.GetOrCreate(
            key,
            () => new ResultControlSurface(
                size,
                key.PixelWidth,
                key.PixelHeight,
                key.RenderScale)).Render(result);
    }

    private sealed class ResultControlSurface
    {
        private readonly double _width;
        private readonly double _height;
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;
        private readonly int _renderScale;
        private readonly Border _border;
        private FrameworkElement? _content;
        private ScrollViewer? _scrollViewer;
        private TextBox? _textBox;
        private FlowDocumentScrollViewer? _markdownViewer;
        private ChatViewerSurface? _chatViewer;
        private ResultControlKind? _kind;
        private string? _renderedText;
        private bool _arranged;

        public ResultControlSurface(
            Size size,
            int pixelWidth,
            int pixelHeight,
            int renderScale)
        {
            _width = Math.Max(8, size.Width);
            _height = Math.Max(8, size.Height);
            _pixelWidth = Math.Max(8, pixelWidth);
            _pixelHeight = Math.Max(8, pixelHeight);
            _renderScale = Math.Max(1, renderScale);
            _border = new Border
            {
                Width = _width,
                Height = _height,
                Background = new SolidColorBrush(Color.FromArgb(225, 24, 31, 29)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(ResultCornerRadius),
                ClipToBounds = true
            };
        }

        public ResultControlFrame Render(ResultOverlaySnapshot result)
        {
            bool layoutChanged;
            try
            {
                layoutChanged = UpdateContent(result);
            }
            catch
            {
                layoutChanged = UseTextContent(
                    string.IsNullOrWhiteSpace(result.VisibleText)
                        ? result.Text
                        : result.VisibleText);
            }

            if (!_arranged || layoutChanged)
            {
                _border.ApplyTemplate();
                _content?.ApplyTemplate();
                _border.Measure(new Size(_width, _height));
                _border.Arrange(new Rect(0, 0, _width, _height));
                _border.UpdateLayout();
                _scrollViewer = _content as ScrollViewer ??
                                (_content is null ? null : FindVisualChild<ScrollViewer>(_content));
                _arranged = true;
            }

            if (_scrollViewer is not null &&
                Math.Abs(_scrollViewer.VerticalOffset - result.ScrollOffset) >= 0.1)
            {
                _scrollViewer.ScrollToVerticalOffset(result.ScrollOffset);
                _border.UpdateLayout();
            }

            var bitmap = new RenderTargetBitmap(
                _pixelWidth,
                _pixelHeight,
                96d * _renderScale,
                96d * _renderScale,
                PixelFormats.Pbgra32);
            bitmap.Render(_border);
            bitmap.Freeze();
            return new ResultControlFrame(bitmap, _scrollViewer?.ScrollableHeight ?? 0);
        }

        private bool UpdateContent(ResultOverlaySnapshot result)
        {
            var nextKind = result.Chat is not null
                ? ResultControlKind.Chat
                : result.ContentFormat == ResultContentFormat.Markdown &&
                  result.Status != ResultStatus.Streaming
                    ? ResultControlKind.Markdown
                    : ResultControlKind.Text;
            if (_kind != nextKind || _content is null)
            {
                _kind = nextKind;
                _renderedText = result.Text;
                _textBox = null;
                _markdownViewer = null;
                _chatViewer = null;
                _content = nextKind switch
                {
                    ResultControlKind.Chat =>
                        (_chatViewer = new ChatViewerSurface((int)Math.Ceiling(_width))).Root,
                    ResultControlKind.Markdown =>
                        _markdownViewer = CreateMarkdownViewer(result.Text),
                    _ => _textBox = CreateTextBox(result.Text)
                };
                _border.Child = _content;
                _arranged = false;
                if (_chatViewer is not null && result.Chat is { } initialChat)
                {
                    _chatViewer.Update(initialChat, result.Status);
                }
                return true;
            }

            switch (nextKind)
            {
                case ResultControlKind.Chat when result.Chat is { } chat:
                    return _chatViewer?.Update(chat, result.Status) == true;
                case ResultControlKind.Markdown:
                    if (!string.Equals(_renderedText, result.Text, StringComparison.Ordinal))
                    {
                        _markdownViewer!.Document =
                            ResultContentFormatter.CreateMarkdownDocument(result.Text);
                        _renderedText = result.Text;
                        return true;
                    }
                    break;
                case ResultControlKind.Text:
                    if (!string.Equals(_renderedText, result.Text, StringComparison.Ordinal))
                    {
                        _textBox!.Text = result.Text;
                        _renderedText = result.Text;
                        return true;
                    }
                    break;
            }
            return false;
        }

        private bool UseTextContent(string text)
        {
            if (_kind == ResultControlKind.Text && _textBox is not null)
            {
                if (string.Equals(_renderedText, text, StringComparison.Ordinal))
                {
                    return false;
                }
                _textBox.Text = text;
                _renderedText = text;
                return true;
            }

            _kind = ResultControlKind.Text;
            _renderedText = text;
            _markdownViewer = null;
            _chatViewer = null;
            _textBox = CreateTextBox(text);
            _content = _textBox;
            _border.Child = _content;
            _arranged = false;
            return true;
        }
    }

    private sealed class ResultControlSurfaceCache
    {
        private readonly Dictionary<ResultControlFrameCacheKey, ResultControlSurface> _surfaces = [];

        public ResultControlSurface GetOrCreate(
            ResultControlFrameCacheKey key,
            Func<ResultControlSurface> factory)
        {
            if (_surfaces.TryGetValue(key, out var surface))
            {
                return surface;
            }

            surface = factory();
            _surfaces.Add(key, surface);
            return surface;
        }
    }

    private enum ResultControlKind
    {
        Text,
        Markdown,
        Chat
    }

    private static TextBox CreateTextBox(string text)
    {
        var textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(244, 248, 246)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 12, 10, 12),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 18,
            IsTabStop = false
        };
        ScrollViewer.SetCanContentScroll(textBox, false);
        return textBox;
    }

    private sealed class ChatViewerSurface
    {
        private readonly int _width;
        private readonly StackPanel _messages;
        private readonly List<ChatMessageSurface> _messageSurfaces = [];

        public ChatViewerSurface(int width)
        {
            _width = width;
            Root = new Grid
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 31, 29)),
                ClipToBounds = true
            };
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 40, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 108, 102)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(15, 11, 15, 10),
                Child = new TextBlock
                {
                    Text = AppLocalization.Text("Chat.Title"),
                    FontFamily = new FontFamily("Microsoft YaHei UI"),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(246, 249, 248))
                }
            });

            _messages = new StackPanel
            {
                Margin = new Thickness(12, 10, 12, 14),
                Background = Brushes.Transparent
            };
            var scrollViewer = new ScrollViewer
            {
                Content = _messages,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsTabStop = false
            };
            ScrollViewer.SetCanContentScroll(scrollViewer, false);
            Grid.SetRow(scrollViewer, 1);
            Root.Children.Add(scrollViewer);
        }

        public Grid Root { get; }

        public bool Update(AssistantConversationView chat, ResultStatus status)
        {
            var descriptors = new List<ChatMessageDescriptor>((chat.Turns.Count * 2) + 2);
            foreach (var turn in chat.Turns)
            {
                descriptors.Add(new ChatMessageDescriptor(
                    AppLocalization.Text("Chat.User"),
                    turn.User,
                    IsUser: true,
                    ParseMarkdown: false));
                descriptors.Add(new ChatMessageDescriptor(
                    AppLocalization.Text("Chat.Assistant"),
                    turn.Assistant,
                    IsUser: false,
                    ParseMarkdown: true));
            }
            if (!string.IsNullOrWhiteSpace(chat.PendingQuestion))
            {
                descriptors.Add(new ChatMessageDescriptor(
                    AppLocalization.Text("Chat.User"),
                    chat.PendingQuestion,
                    IsUser: true,
                    ParseMarkdown: false));
            }
            if (!string.IsNullOrWhiteSpace(chat.PendingAnswer))
            {
                descriptors.Add(new ChatMessageDescriptor(
                    AppLocalization.Text("Chat.Assistant"),
                    chat.PendingAnswer,
                    IsUser: false,
                    ParseMarkdown: status != ResultStatus.Streaming));
            }

            var changed = false;
            for (var index = 0; index < descriptors.Count; index++)
            {
                var descriptor = descriptors[index];
                if (index >= _messageSurfaces.Count)
                {
                    var added = new ChatMessageSurface(descriptor, _width);
                    _messageSurfaces.Add(added);
                    _messages.Children.Add(added.Root);
                    changed = true;
                    continue;
                }

                var surface = _messageSurfaces[index];
                if (!surface.CanUpdate(descriptor))
                {
                    var replacement = new ChatMessageSurface(descriptor, _width);
                    _messageSurfaces[index] = replacement;
                    _messages.Children[index] = replacement.Root;
                    changed = true;
                }
                else if (surface.Update(descriptor))
                {
                    changed = true;
                }
            }
            while (_messageSurfaces.Count > descriptors.Count)
            {
                var last = _messageSurfaces.Count - 1;
                _messageSurfaces.RemoveAt(last);
                _messages.Children.RemoveAt(last);
                changed = true;
            }
            return changed;
        }
    }

    private sealed class ChatMessageSurface
    {
        private readonly TextBlock _content;
        private ChatMessageDescriptor _descriptor;

        public ChatMessageSurface(ChatMessageDescriptor descriptor, int width)
        {
            _descriptor = descriptor;
            _content = new TextBlock
            {
                Text = DisplayText(descriptor),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 16,
                LineHeight = 23,
                Foreground = new SolidColorBrush(Color.FromRgb(246, 249, 248))
            };
            var bubble = new Border
            {
                Background = new SolidColorBrush(descriptor.IsUser
                    ? Color.FromRgb(30, 79, 70)
                    : Color.FromRgb(39, 48, 45)),
                BorderBrush = new SolidColorBrush(descriptor.IsUser
                    ? Color.FromRgb(77, 224, 193)
                    : Color.FromRgb(101, 117, 111)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 8, 11, 9),
                Child = _content
            };
            Root = new StackPanel
            {
                HorizontalAlignment = descriptor.IsUser
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                MaxWidth = Math.Max(120, width * 0.82),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Root.Children.Add(new TextBlock
            {
                Text = descriptor.Role,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(188, 201, 196)),
                HorizontalAlignment = descriptor.IsUser
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 4, 4)
            });
            Root.Children.Add(bubble);
        }

        public StackPanel Root { get; }

        public bool CanUpdate(ChatMessageDescriptor descriptor) =>
            descriptor.IsUser == _descriptor.IsUser &&
            string.Equals(descriptor.Role, _descriptor.Role, StringComparison.Ordinal);

        public bool Update(ChatMessageDescriptor descriptor)
        {
            if (descriptor == _descriptor)
            {
                return false;
            }
            _descriptor = descriptor;
            _content.Text = DisplayText(descriptor);
            return true;
        }

        private static string DisplayText(ChatMessageDescriptor descriptor)
        {
            var text = descriptor.ParseMarkdown
                ? VisibleChatText(descriptor.Text)
                : descriptor.Text;
            return string.IsNullOrWhiteSpace(text) ? "..." : text.Trim();
        }
    }

    private sealed record ChatMessageDescriptor(
        string Role,
        string Text,
        bool IsUser,
        bool ParseMarkdown);

    private static string VisibleChatText(string text)
    {
        try
        {
            return ResultContentFormatter.ExtractMarkdownVisibleText(text);
        }
        catch
        {
            return ResultContentFormatter.NormalizeVisibleText(text);
        }
    }

    private static FlowDocumentScrollViewer CreateMarkdownViewer(string markdown)
    {
        var viewer = new FlowDocumentScrollViewer
        {
            Document = ResultContentFormatter.CreateMarkdownDocument(markdown),
            IsToolBarVisible = false,
            IsSelectionEnabled = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsTabStop = false
        };
        ScrollViewer.SetCanContentScroll(viewer, false);
        return viewer;
    }

    private BitmapSource DecodeBitmap(byte[] image) =>
        _decodedImages.GetValue(image, static bytes => DecodeBitmapCore(bytes));

    private static BitmapSource DecodeBitmapCore(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.HighQuality);
        bitmap.Freeze();
        return bitmap;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void DrawArmedHint(DrawingContext drawing)
    {
        var bounds = new Rect(86, 222, 340, 68);
        drawing.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(224, 42, 45, 48)),
            new Pen(new SolidColorBrush(Color.FromArgb(235, 238, 241, 243)), 1.5),
            bounds,
            6,
            6);
        var text = Text(AppLocalization.Text("Overlay.Selection.Armed"), 19, Colors.White, bounds.Width - 24);
        text.TextAlignment = TextAlignment.Center;
        drawing.DrawText(text, new Point(bounds.Left + 12, bounds.Top + 20));
    }

    private static Pen? InteractionPen(bool highlighted) => highlighted
        ? new Pen(new SolidColorBrush(Color.FromArgb(255, 46, 229, 140)), 1.25)
        : null;

    private void DrawToolbar(
        DrawingContext drawing,
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        OverlayPointerVisual? pointer,
        int renderScale,
        bool showPrimaryToolbar,
        bool isCommandRecording,
        OverlayToolbarSide toolbarSide)
    {
        var hoveredAction = pointer is { } value
            ? OverlayToolbarLayout.HitTest(kind, plane, value.TexturePoint, toolbarSide)
            : null;
        if (showPrimaryToolbar)
        {
            var bounds = OverlayToolbarLayout.CalculateBounds(kind, plane, toolbarSide);
            if (!bounds.IsEmpty)
            {
                var toolbarKey = (kind, toolbarSide);
                if (!_toolbars.TryGetValue(toolbarKey, out var toolbar))
                {
                    toolbar = new WpfOverlayToolbar(kind, toolbarSide);
                    _toolbars.Add(toolbarKey, toolbar);
                }
                var bitmap = toolbar.Render(
                    Math.Max(1, (int)Math.Ceiling(bounds.Width)),
                    Math.Max(1, (int)Math.Ceiling(bounds.Height)),
                    hoveredAction is OverlayToolbarAction.CustomCommand ? null : hoveredAction,
                    pointer?.IsPressed == true,
                    renderScale);
                drawing.DrawImage(bitmap, bounds);
            }
        }

        if (!OverlayToolbarLayout.SupportsVoiceButton(kind) ||
            (!showPrimaryToolbar && !isCommandRecording))
        {
            return;
        }

        var voiceBounds = OverlayToolbarLayout.CalculateVoiceBounds(kind, plane, toolbarSide);
        if (voiceBounds.IsEmpty)
        {
            return;
        }
        var voiceKey = (kind, toolbarSide);
        if (!_voiceButtons.TryGetValue(voiceKey, out var voiceButton))
        {
            voiceButton = new WpfOverlayToolbar([OverlayToolbarAction.CustomCommand]);
            _voiceButtons.Add(voiceKey, voiceButton);
        }
        var voiceBitmap = voiceButton.Render(
            Math.Max(1, (int)Math.Ceiling(voiceBounds.Width)),
            Math.Max(1, (int)Math.Ceiling(voiceBounds.Height)),
            hoveredAction == OverlayToolbarAction.CustomCommand
                ? OverlayToolbarAction.CustomCommand
                : null,
            pointer?.IsPressed == true,
            renderScale,
            isCommandRecording);
        drawing.DrawImage(voiceBitmap, voiceBounds);
    }

    private static void DrawCloseHoldProgress(
        DrawingContext drawing,
        SpatialSelectionPlane plane,
        double? progress)
    {
        if (progress is null)
        {
            return;
        }

        var panel = CalculateResultPanel(plane);
        var size = CalculateCloseHoldProgressSize(plane);
        var center = new Point(panel.Left + (panel.Width / 2), panel.Top + (panel.Height / 2));
        DrawCloseHoldProgressAt(drawing, center, size, progress.Value);
    }

    private static void DrawCloseHoldProgressAt(
        DrawingContext drawing,
        Point center,
        double size,
        double progress)
    {
        var radius = (size / 2) - 5;
        var background = new SolidColorBrush(Color.FromArgb(205, 38, 41, 44));
        var trackPen = new Pen(new SolidColorBrush(Color.FromArgb(125, 255, 255, 255)), 3);
        var progressPen = new Pen(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawing.DrawEllipse(background, trackPen, center, size / 2, size / 2);

        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped >= 1)
        {
            drawing.DrawEllipse(null, progressPen, center, radius, radius);
        }
        else if (clamped > 0)
        {
            drawing.DrawGeometry(null, progressPen, CreateProgressArc(center, radius, clamped));
        }

        var iconSize = size * 0.34;
        drawing.PushTransform(new TranslateTransform(
            center.X - (iconSize / 2),
            center.Y - (iconSize / 2)));
        drawing.PushTransform(new ScaleTransform(iconSize / 24, iconSize / 24));
        drawing.DrawGeometry(Brushes.White, null, MaterialIconPaths.DeleteOutline);
        drawing.Pop();
        drawing.Pop();
    }

    internal static double CalculateCloseHoldProgressLogicalExtent(
        SpatialSelectionPlane plane) =>
        CalculateCloseHoldProgressSize(plane) + 10;

    private static double CalculateCloseHoldProgressSize(SpatialSelectionPlane plane)
    {
        var panel = CalculateResultPanel(plane);
        return Math.Clamp(Math.Min(panel.Width, panel.Height) * 0.17, 42, 70);
    }

    private static Geometry CreateProgressArc(Point center, double radius, double progress)
    {
        var start = new Point(center.X, center.Y - radius);
        var angle = (Math.PI * 2 * progress) - (Math.PI / 2);
        var end = new Point(
            center.X + (Math.Cos(angle) * radius),
            center.Y + (Math.Sin(angle) * radius));
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                progress > 0.5,
                SweepDirection.Clockwise,
                true,
                false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawPointer(
        DrawingContext drawing,
        OverlayPointerVisual? pointer,
        Size logicalSize)
    {
        if (pointer is not { } value)
        {
            return;
        }

        var center = new Point(
            value.TexturePoint.X * logicalSize.Width,
            value.TexturePoint.Y * logicalSize.Height);
        var outer = new Pen(new SolidColorBrush(Color.FromArgb(235, 35, 39, 42)), 5);
        var inner = new Pen(Brushes.White, value.IsPressed ? 4 : 2.5);
        drawing.DrawEllipse(
            value.IsPressed ? new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)) : null,
            outer,
            center,
            11,
            11);
        drawing.DrawEllipse(null, inner, center, 8, 8);
        drawing.DrawEllipse(Brushes.White, null, center, 2.2, 2.2);
    }

    internal static Rect CalculateResultPanel(SpatialSelectionPlane plane)
    {
        var canvas = CalculateLogicalCanvasSize(plane);
        return new Rect(0, 0, canvas.Width, canvas.Height);
    }

    internal static Size CalculateLogicalCanvasSize(SpatialSelectionPlane plane)
    {
        var aspectRatio = plane.Width > 0 && plane.Height > 0
            ? Math.Clamp(plane.Width / plane.Height, 1f / 32f, 32f)
            : 1f;
        return aspectRatio >= 1
            ? new Size(Width, Math.Max(16, Height / aspectRatio))
            : new Size(Math.Max(16, Width * aspectRatio), Height);
    }

    internal static OverlayRenderSize CalculateRenderSize(
        SpatialSelectionPlane plane,
        int textureLongEdge)
    {
        if (textureLongEdge is not (SmallTextureLongEdge or DefaultTextureLongEdge or HtmlTextureLongEdge))
        {
            throw new ArgumentOutOfRangeException(
                nameof(textureLongEdge),
                textureLongEdge,
                "纹理长边必须是 512、1024 或 2048。");
        }

        var logicalSize = CalculateLogicalCanvasSize(plane);
        var renderScale = textureLongEdge / Width;
        return new OverlayRenderSize(
            logicalSize,
            Math.Max(8, (int)Math.Round(logicalSize.Width * renderScale)),
            Math.Max(8, (int)Math.Round(logicalSize.Height * renderScale)),
            renderScale);
    }

    internal static int SelectTextureLongEdge(
        int sourcePixelWidth,
        int sourcePixelHeight,
        int maximumLongEdge)
    {
        var sourceLongEdge = Math.Max(
            Math.Max(1, sourcePixelWidth),
            Math.Max(1, sourcePixelHeight));
        if (sourceLongEdge <= SmallTextureLongEdge)
        {
            return SmallTextureLongEdge;
        }

        if (sourceLongEdge <= DefaultTextureLongEdge || maximumLongEdge <= DefaultTextureLongEdge)
        {
            return DefaultTextureLongEdge;
        }

        return HtmlTextureLongEdge;
    }

    private static void DrawCornerHandles(DrawingContext drawing, Rect rectangle, Color color)
    {
        var brush = new SolidColorBrush(color);
        const double size = 10;
        foreach (var point in new[]
                 {
                     rectangle.TopLeft,
                     rectangle.TopRight,
                     rectangle.BottomLeft,
                     rectangle.BottomRight
                 })
        {
            drawing.DrawRectangle(
                brush,
                null,
                new Rect(point.X - size / 2, point.Y - size / 2, size, size));
        }
    }

    private static void DrawSelectionGrid(DrawingContext drawing, Rect rectangle, Color color)
    {
        var gridColor = Color.FromArgb(52, color.R, color.G, color.B);
        var gridPen = new Pen(new SolidColorBrush(gridColor), 1.2);
        var cellSize = Math.Clamp(Math.Min(rectangle.Width, rectangle.Height) / 5d, 24d, 64d);
        for (var x = rectangle.Left + cellSize; x < rectangle.Right; x += cellSize)
        {
            drawing.DrawLine(gridPen, new Point(x, rectangle.Top), new Point(x, rectangle.Bottom));
        }

        for (var y = rectangle.Top + cellSize; y < rectangle.Bottom; y += cellSize)
        {
            drawing.DrawLine(gridPen, new Point(rectangle.Left, y), new Point(rectangle.Right, y));
        }

        drawing.DrawRectangle(
            null,
            new Pen(new SolidColorBrush(color), 4),
            rectangle);
    }

    private void DrawSelectionHint(
        DrawingContext drawing,
        Rect frame,
        string hint,
        Color accent)
    {
        var bounds = CalculateSelectionHintBounds(frame);
        if (bounds.IsEmpty)
        {
            return;
        }

        var background = new SolidColorBrush(Color.FromArgb(220, 38, 41, 44));
        var border = new Pen(new SolidColorBrush(Color.FromArgb(235, accent.R, accent.G, accent.B)), 1.5);
        drawing.DrawRoundedRectangle(background, border, bounds, 6, 6);

        var fontSize = bounds.Width switch
        {
            >= 280 => 15,
            >= 190 => 13,
            _ => 11
        };
        var formatted = Text(hint, fontSize, Colors.White, Math.Max(1, bounds.Width - 14));
        formatted.TextAlignment = TextAlignment.Center;
        formatted.MaxTextHeight = Math.Max(1, bounds.Height - 6);
        var y = bounds.Top + Math.Max(2, (bounds.Height - formatted.Height) / 2);
        drawing.DrawText(formatted, new Point(bounds.Left + 7, y));
    }

    internal static Rect CalculateSelectionHintBounds(Rect frame)
    {
        const double canvasMargin = 4;
        const double frameGap = 5;
        const double desiredHeight = 36;
        const double minimumHeight = 18;
        const double minimumWidth = 72;

        var width = Math.Min(frame.Width, Width - (canvasMargin * 2));
        var availableHeight = frame.Top - frameGap - canvasMargin;
        var height = Math.Min(desiredHeight, availableHeight);
        if (width < minimumWidth || height < minimumHeight)
        {
            return Rect.Empty;
        }

        var left = Math.Clamp(
            frame.Left + ((frame.Width - width) / 2),
            canvasMargin,
            Width - canvasMargin - width);
        return new Rect(left, frame.Top - frameGap - height, width, height);
    }

    private static string SelectionHint(
        SelectionState state,
        bool frameUsable,
        string? invalidHint) =>
        !frameUsable
            ? invalidHint ?? AppLocalization.Text("Overlay.Selection.Invalid")
            : state == SelectionState.Locked
                ? AppLocalization.Text("Overlay.Selection.Capturing")
                : AppLocalization.Text("Overlay.Selection.Release");

    private FormattedText Text(string value, double size, Color color, double maximumWidth)
    {
        var text = new FormattedText(
            value,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _typeface,
            size,
            new SolidColorBrush(color),
            1.0)
        {
            MaxTextWidth = Math.Max(1, maximumWidth),
            MaxTextHeight = Height - 100,
            Trimming = TextTrimming.WordEllipsis
        };
        return text;
    }

    private static OverlayRenderFrame RenderVisual(
        DrawingVisual visual,
        OverlayRenderSize renderSize)
    {
        var textureWidth = renderSize.PixelWidth;
        var textureHeight = renderSize.PixelHeight;
        var textureDpi = 96d * renderSize.RenderScale;
        var bitmap = new RenderTargetBitmap(
            textureWidth,
            textureHeight,
            textureDpi,
            textureDpi,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = GC.AllocateUninitializedArray<byte>(
            checked(textureWidth * textureHeight * 4));
        bitmap.CopyPixels(pixels, textureWidth * 4, 0);
        ConvertPbgraToRgbaInPlace(pixels);
        return new OverlayRenderFrame(pixels, textureWidth, textureHeight);
    }

    private static byte[] CopyBitmapPixels(BitmapSource bitmap, double opacity)
    {
        var pixels = GC.AllocateUninitializedArray<byte>(
            checked(bitmap.PixelWidth * bitmap.PixelHeight * 4));
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        ConvertPbgraToRgbaInPlace(pixels);
        if (opacity < 1)
        {
            for (var index = 3; index < pixels.Length; index += 4)
            {
                pixels[index] = (byte)Math.Clamp(
                    (int)Math.Round(pixels[index] * opacity),
                    0,
                    byte.MaxValue);
            }
        }
        return pixels;
    }

    internal static void ConvertPbgraToRgbaInPlace(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
        {
            throw new ArgumentException("像素缓冲区长度必须是 4 的倍数。", nameof(pixels));
        }

        for (var index = 0; index < pixels.Length; index += 4)
        {
            var blue = pixels[index];
            var green = pixels[index + 1];
            var red = pixels[index + 2];
            var alpha = pixels[index + 3];
            var lookupOffset = alpha << 8;
            pixels[index] = UnpremultiplyLookup[lookupOffset | red];
            pixels[index + 1] = UnpremultiplyLookup[lookupOffset | green];
            pixels[index + 2] = UnpremultiplyLookup[lookupOffset | blue];
        }
    }

    private static byte[] CreateUnpremultiplyLookup()
    {
        var lookup = new byte[ushort.MaxValue + 1];
        for (var alpha = 1; alpha <= byte.MaxValue; alpha++)
        {
            var offset = alpha << 8;
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                lookup[offset | value] = (byte)Math.Clamp((value * 255) / alpha, 0, 255);
            }
        }
        return lookup;
    }

    private static T OnUiThread<T>(Func<T> action)
    {
        var application = Application.Current;
        if (application is not null)
        {
            return application.Dispatcher.CheckAccess()
                ? action()
                : application.Dispatcher.Invoke(action);
        }

        return Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? action()
            : FallbackDispatcher.Value.Invoke(action);
    }

    private static Dispatcher CreateFallbackDispatcher()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "SteamVRTranslator.OverlayRenderer.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher ?? throw new InvalidOperationException("Unable to create the overlay STA dispatcher.");
    }
}

internal static class OverlayToolbarLayout
{
    private const double OuterMargin = 5;
    internal const double ContentPadding = 5;
    internal const double ButtonGap = 6;
    private const double PreferredButtonSize = 30;
    private const double MinimumButtonSize = 12;

    public static IReadOnlyList<OverlayToolbarAction> PrimaryActions(
        InteractiveOverlayKind kind,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
    {
        IReadOnlyList<OverlayToolbarAction> actions = kind switch
        {
            InteractiveOverlayKind.Capture =>
                [
                    OverlayToolbarAction.Translate,
                    OverlayToolbarAction.LayoutTranslate,
                    OverlayToolbarAction.Close
                ],
            InteractiveOverlayKind.Result =>
                [
                    OverlayToolbarAction.Send,
                    OverlayToolbarAction.Close
                ],
            _ => [OverlayToolbarAction.Close]
        };
        return side == OverlayToolbarSide.Left
            ? actions.Reverse().ToArray()
            : actions;
    }

    public static IReadOnlyList<OverlayToolbarAction> Actions(
        InteractiveOverlayKind kind,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
    {
        var primary = PrimaryActions(kind, side);
        return SupportsVoiceButton(kind)
            ? [.. primary, OverlayToolbarAction.CustomCommand]
            : primary;
    }

    public static bool SupportsVoiceButton(InteractiveOverlayKind kind) =>
        kind is InteractiveOverlayKind.Capture or InteractiveOverlayKind.Result;

    public static Rect CalculateBounds(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
    {
        var panel = OverlayRenderer.CalculateResultPanel(plane);
        var actionCount = PrimaryActions(kind, side).Count;
        var availablePanelWidth = panel.Width - (OuterMargin * 2);
        var availablePanelHeight = panel.Height - (OuterMargin * 2);
        var availableButtonWidth =
            (availablePanelWidth - (ContentPadding * 2) - (ButtonGap * (actionCount - 1))) / actionCount;
        var buttonSize = Math.Floor(Math.Min(
            PreferredButtonSize,
            Math.Min(availablePanelHeight - (ContentPadding * 2), availableButtonWidth)));
        if (buttonSize < MinimumButtonSize)
        {
            return Rect.Empty;
        }

        var width = (ContentPadding * 2) +
                    (buttonSize * actionCount) +
                    (ButtonGap * (actionCount - 1));
        var height = (ContentPadding * 2) + buttonSize;
        var left = side == OverlayToolbarSide.Left
            ? panel.Left + OuterMargin
            : panel.Right - OuterMargin - width;
        return new Rect(
            left,
            panel.Top + OuterMargin,
            width,
            height);
    }

    public static Rect CalculateButtonBounds(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        OverlayToolbarAction action,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
    {
        if (action == OverlayToolbarAction.CustomCommand)
        {
            var voice = CalculateVoiceBounds(kind, plane, side);
            return voice.IsEmpty
                ? Rect.Empty
                : new Rect(
                    voice.Left + ContentPadding,
                    voice.Top + ContentPadding,
                    voice.Width - (ContentPadding * 2),
                    voice.Height - (ContentPadding * 2));
        }

        var actions = PrimaryActions(kind, side);
        var actionIndex = -1;
        for (var index = 0; index < actions.Count; index++)
        {
            if (actions[index] == action)
            {
                actionIndex = index;
                break;
            }
        }
        var toolbar = CalculateBounds(kind, plane, side);
        if (actionIndex < 0 || toolbar.IsEmpty)
        {
            return Rect.Empty;
        }

        var buttonSize = toolbar.Height - (ContentPadding * 2);
        return new Rect(
            toolbar.Left + ContentPadding + (actionIndex * (buttonSize + ButtonGap)),
            toolbar.Top + ContentPadding,
            buttonSize,
            buttonSize);
    }

    public static Rect CalculateVoiceBounds(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
    {
        if (!SupportsVoiceButton(kind))
        {
            return Rect.Empty;
        }

        var panel = OverlayRenderer.CalculateResultPanel(plane);
        var available = Math.Min(
            panel.Width - (OuterMargin * 2) - (ContentPadding * 2),
            panel.Height - (OuterMargin * 2) - (ContentPadding * 2));
        var buttonSize = Math.Floor(Math.Min(PreferredButtonSize, available));
        if (buttonSize < MinimumButtonSize)
        {
            return Rect.Empty;
        }

        var extent = buttonSize + (ContentPadding * 2);
        var left = side == OverlayToolbarSide.Left
            ? panel.Left + OuterMargin
            : panel.Right - OuterMargin - extent;
        return new Rect(
            left,
            panel.Bottom - OuterMargin - extent,
            extent,
            extent);
    }

    public static OverlayToolbarAction? HitTest(
        InteractiveOverlayKind kind,
        SpatialSelectionPlane plane,
        NormalizedPoint texturePoint,
        OverlayToolbarSide side = OverlayToolbarSide.Right)
    {
        var point = new Point(
            texturePoint.X * OverlayRenderer.CalculateLogicalCanvasSize(plane).Width,
            texturePoint.Y * OverlayRenderer.CalculateLogicalCanvasSize(plane).Height);
        foreach (var action in Actions(kind, side))
        {
            if (CalculateButtonBounds(kind, plane, action, side).Contains(point))
            {
                return action;
            }
        }

        return null;
    }
}

internal sealed record ResultRenderFrame(
    byte[] Pixels,
    double MaximumScrollOffset,
    int PixelWidth,
    int PixelHeight);

internal sealed record OverlayRenderFrame(
    byte[] Pixels,
    int PixelWidth,
    int PixelHeight);

internal readonly record struct OverlayRenderSize(
    Size LogicalSize,
    int PixelWidth,
    int PixelHeight,
    int RenderScale)
{
    public static OverlayRenderSize Square(int textureLongEdge) =>
        new(
            new Size(OverlayRenderer.Width, OverlayRenderer.Height),
            textureLongEdge,
            textureLongEdge,
            textureLongEdge / OverlayRenderer.Width);
}

internal sealed record ResultControlFrame(BitmapSource Bitmap, double MaximumScrollOffset);

internal readonly record struct ResultControlFrameCacheKey(
    int PixelWidth,
    int PixelHeight,
    int RenderScale);
