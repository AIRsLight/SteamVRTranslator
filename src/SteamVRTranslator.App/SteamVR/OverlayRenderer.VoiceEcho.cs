using System.Windows.Controls;
using System.Windows.Media;
using SteamVRTranslator.App.Output;

namespace SteamVRTranslator.App.SteamVR;

internal sealed partial class OverlayRenderer
{
    public const int VoiceEchoWidth = 2048;
    public const int VoiceEchoHeight = 640;
    internal const int VoiceEchoMinimumWidth = 512;
    internal const double VoiceEchoFontSize = 52;

    public OverlayRenderFrame RenderVoiceEcho(VoiceInputEchoSnapshot snapshot) => OnUiThread(() =>
    {
        var text = snapshot.Text;
        var tail = TextChunker.Tail(text, 144);
        if (tail.Length < text.Trim().Length) tail = "…" + tail;
        tail = tail.Replace('\r', ' ').Replace('\n', ' ');
        var body = new TextBlock
        {
            Text = tail, FontFamily = _typeface.FontFamily, FontSize = VoiceEchoFontSize,
            Foreground = OverlayTheme.Brush(OverlayColorRole.Text, "#FFFFFF"), TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false, Focusable = false
        };
        body.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Clamp(AlignEchoSize(body.DesiredSize.Width + 96), VoiceEchoMinimumWidth, VoiceEchoWidth);
        var bodyWidth = width - 96;
        body.Measure(new Size(bodyWidth, double.PositiveInfinity));
        var height = Math.Clamp(AlignEchoSize(body.DesiredSize.Height + 64), 128, VoiceEchoHeight);
        body.Arrange(new Rect(0, 0, bodyWidth, body.DesiredSize.Height));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRoundedRectangle(new SolidColorBrush(OverlayTheme.Resolve(OverlayColorRole.Background, Color.FromArgb(225, 18, 27, 32))),
                new Pen(new SolidColorBrush(OverlayTheme.Resolve(OverlayColorRole.Border, Color.FromArgb(140, 127, 185, 168))), 2),
                new Rect(2, 2, width - 4, height - 4), 24, 24);
            drawing.PushClip(new RectangleGeometry(new Rect(48, 32, bodyWidth, height - 64)));
            var bodyBrush = new VisualBrush(body)
            {
                AutoLayoutContent = false,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, bodyWidth, body.DesiredSize.Height)
            };
            drawing.DrawRectangle(bodyBrush, null, new Rect(48, 32, bodyWidth, body.DesiredSize.Height));
            drawing.Pop();
        }
        return RenderVisual(visual, new OverlayRenderSize(new Size(width, height), width, height, 1));
    });

    private static int AlignEchoSize(double value) => checked((int)Math.Ceiling(value / 16) * 16);

}
