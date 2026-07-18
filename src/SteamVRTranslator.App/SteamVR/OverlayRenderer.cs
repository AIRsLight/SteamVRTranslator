using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.IO;
using System.Windows.Threading;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.App.SteamVR;

internal sealed class OverlayRenderer
{
    public const int Width = 512;
    public const int Height = 512;

    private readonly Typeface _typeface = new("Microsoft YaHei UI");
    private static readonly Lazy<Dispatcher> FallbackDispatcher = new(CreateFallbackDispatcher);

    public byte[] RenderSelection(SelectionSnapshot snapshot, bool frameUsable = true) =>
        OnUiThread(() => RenderSelectionCore(snapshot, frameUsable));

    public ResultRenderFrame RenderResults(
        ResultOverlaySnapshot snapshot,
        SpatialSelectionPlane plane,
        bool highlighted = false,
        OverlayPointerVisual? pointer = null) =>
        OnUiThread(() => RenderResultsCore(snapshot, plane, highlighted, pointer));

    public byte[] RenderCapture(
        byte[] jpeg,
        SpatialSelectionPlane plane,
        bool highlighted = false,
        OverlayPointerVisual? pointer = null) =>
        OnUiThread(() => RenderCaptureCore(jpeg, plane, highlighted, pointer));

    public byte[] RenderWindow(
        WpfWindowOverlaySource source,
        SpatialSelectionPlane plane,
        bool highlighted = false,
        OverlayPointerVisual? pointer = null) =>
        OnUiThread(() => RenderWindowCore(source, plane, highlighted, pointer));

    private byte[] RenderSelectionCore(SelectionSnapshot snapshot, bool frameUsable)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var accent = !frameUsable && snapshot.Region is not null
                ? Color.FromRgb(239, 68, 68)
                : snapshot.State == SelectionState.Locked
                    ? Color.FromRgb(46, 229, 140)
                    : Color.FromRgb(255, 213, 74);
            if (snapshot.Region is { } region)
            {
                var rectangle = new Rect(
                    region.Left * Width,
                    region.Top * Height,
                    region.Width * Width,
                    region.Height * Height);
                DrawSelectionGrid(drawing, rectangle, accent);
                DrawCornerHandles(drawing, rectangle, accent);
                DrawSelectionHint(drawing, rectangle, SelectionHint(snapshot.State, frameUsable), accent);
            }
            else if (snapshot.State == SelectionState.Armed)
            {
                DrawArmedHint(drawing);
            }
        }

        return RenderVisual(visual);
    }

    private ResultRenderFrame RenderResultsCore(
        ResultOverlaySnapshot snapshot,
        SpatialSelectionPlane plane,
        bool highlighted,
        OverlayPointerVisual? pointer)
    {
        var visual = new DrawingVisual();
        double maximumScroll;
        using (var drawing = visual.RenderOpen())
        {
            maximumScroll = DrawResultPanel(drawing, snapshot, plane, highlighted);
            DrawPointer(drawing, pointer);
        }

        return new ResultRenderFrame(RenderVisual(visual), maximumScroll);
    }

    private byte[] RenderCaptureCore(
        byte[] jpeg,
        SpatialSelectionPlane plane,
        bool highlighted,
        OverlayPointerVisual? pointer)
    {
        using var stream = new MemoryStream(jpeg, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var panel = CalculateResultPanel(plane);
            drawing.DrawImage(bitmap, panel);
            drawing.DrawRectangle(
                null,
                HighlightPen(highlighted),
                panel);
            DrawPointer(drawing, pointer);
        }

        return RenderVisual(visual);
    }

    private byte[] RenderWindowCore(
        WpfWindowOverlaySource source,
        SpatialSelectionPlane plane,
        bool highlighted,
        OverlayPointerVisual? pointer)
    {
        var panel = CalculateResultPanel(plane);
        var bitmap = source.Render(
            Math.Max(8, (int)Math.Ceiling(panel.Width)),
            Math.Max(8, (int)Math.Ceiling(panel.Height)));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(bitmap, panel);
            drawing.DrawRectangle(null, HighlightPen(highlighted), panel);
            DrawPointer(drawing, pointer);
        }

        return RenderVisual(visual);
    }

    private double DrawResultPanel(
        DrawingContext drawing,
        ResultOverlaySnapshot result,
        SpatialSelectionPlane plane,
        bool highlighted)
    {
        var panel = CalculateResultPanel(plane);
        var rendered = RenderResultControl(result, panel.Size, highlighted);
        drawing.DrawImage(rendered.Bitmap, panel);
        return rendered.MaximumScrollOffset;
    }

    private ResultControlFrame RenderResultControl(
        ResultOverlaySnapshot result,
        Size size,
        bool highlighted)
    {
        var width = Math.Max(8, (int)Math.Ceiling(size.Width));
        var height = Math.Max(8, (int)Math.Ceiling(size.Height));
        var status = result.Status switch
        {
            ResultStatus.Streaming => "LIVE",
            ResultStatus.Error => "ERROR",
            _ => "READY"
        };

        var textBox = new TextBox
        {
            Text = result.Text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(28, 33, 36)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 4, 8),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 18,
            IsTabStop = false
        };
        ScrollViewer.SetCanContentScroll(textBox, false);

        var header = new TextBlock
        {
            Text = status,
            Foreground = new SolidColorBrush(Color.FromRgb(70, 76, 80)),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            Margin = new Thickness(12, 7, 12, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(textBox, 1);
        grid.Children.Add(header);
        grid.Children.Add(textBox);

        var border = new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromArgb(218, 238, 241, 243)),
            BorderBrush = highlighted
                ? new SolidColorBrush(Color.FromRgb(46, 229, 140))
                : new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)),
            BorderThickness = new Thickness(highlighted ? 4 : 1.5),
            CornerRadius = new CornerRadius(9),
            ClipToBounds = true,
            Child = grid
        };

        border.Measure(new Size(width, height));
        border.Arrange(new Rect(0, 0, width, height));
        border.ApplyTemplate();
        textBox.ApplyTemplate();
        border.UpdateLayout();
        var scrollViewer = FindVisualChild<ScrollViewer>(textBox);
        scrollViewer?.ScrollToVerticalOffset(result.ScrollOffset);
        border.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(border);
        bitmap.Freeze();
        return new ResultControlFrame(bitmap, scrollViewer?.ScrollableHeight ?? 0);
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
        var text = Text("按住双手扳机开始框选", 19, Colors.White, bounds.Width - 24);
        text.TextAlignment = TextAlignment.Center;
        drawing.DrawText(text, new Point(bounds.Left + 12, bounds.Top + 20));
    }

    private static Pen HighlightPen(bool highlighted) =>
        highlighted
            ? new Pen(new SolidColorBrush(Color.FromArgb(255, 46, 229, 140)), 4)
            : new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1.5);

    private static void DrawPointer(DrawingContext drawing, OverlayPointerVisual? pointer)
    {
        if (pointer is not { } value)
        {
            return;
        }

        var center = new Point(
            value.TexturePoint.X * Width,
            value.TexturePoint.Y * Height);
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
        var panelWidth = Math.Clamp((plane.Width / plane.OverlayExtent) * Width, 8, Width - 8);
        var panelHeight = Math.Clamp((plane.Height / plane.OverlayExtent) * Height, 8, Height - 8);
        return new Rect(
            (Width - panelWidth) / 2,
            (Height - panelHeight) / 2,
            panelWidth,
            panelHeight);
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

    private static string SelectionHint(SelectionState state, bool frameUsable) =>
        !frameUsable
            ? "角度过大，请让相框正对视线"
            : state == SelectionState.Locked
                ? "正在捕获所选区域"
                : "松开任一扳机立即捕获";

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

    private static byte[] RenderVisual(DrawingVisual visual)
    {
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bgra = new byte[Width * Height * 4];
        bitmap.CopyPixels(bgra, Width * 4, 0);
        var rgba = new byte[bgra.Length];
        for (var index = 0; index < bgra.Length; index += 4)
        {
            var alpha = bgra[index + 3];
            rgba[index] = Unpremultiply(bgra[index + 2], alpha);
            rgba[index + 1] = Unpremultiply(bgra[index + 1], alpha);
            rgba[index + 2] = Unpremultiply(bgra[index], alpha);
            rgba[index + 3] = alpha;
        }

        return rgba;
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Clamp((value * 255) / alpha, 0, 255);

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

internal sealed record ResultRenderFrame(byte[] Pixels, double MaximumScrollOffset);

internal sealed record ResultControlFrame(BitmapSource Bitmap, double MaximumScrollOffset);
