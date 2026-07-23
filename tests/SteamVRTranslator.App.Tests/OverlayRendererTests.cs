using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Geometry;
using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class OverlayRendererTests
{
    [WpfRenderingFact]
    public void ArmedSelectionRendersAnEntryHint()
    {
        var renderer = new OverlayRenderer();
        var snapshot = new SelectionSnapshot(
            SelectionState.Armed,
            false,
            false,
            null,
            null,
            null,
            DateTimeOffset.Now);

        var pixels = renderer.RenderSelection(snapshot);

        Assert.Equal(
            OverlayRenderer.TextureWidth * OverlayRenderer.TextureHeight * 4,
            pixels.Length);
        Assert.Contains(
            Enumerable.Range(0, pixels.Length / 4),
            pixel => pixels[(pixel * 4) + 3] > 0);
        Assert.Contains(
            Enumerable.Range(0, pixels.Length / 4),
            pixel => pixel % OverlayRenderer.TextureWidth > OverlayRenderer.Width &&
                     pixels[(pixel * 4) + 3] > 0);
    }

    [Fact]
    public void SingleResultUsesCapturedPlaneAspectRatioAndIsCentered()
    {
        var plane = new SpatialSelectionPlane(
            new Vector3f(0, 1.2f, -0.5f),
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.2f,
            0.1f,
            0.23f,
            new NormalizedPoint(0.1f, 0.2f),
            new NormalizedPoint(0.9f, 0.8f),
            0);

        var panel = OverlayRenderer.CalculateResultPanel(plane);
        var canvas = OverlayRenderer.CalculateLogicalCanvasSize(plane);

        Assert.Equal(2, panel.Width / panel.Height, 5);
        Assert.Equal(canvas.Width / 2d, panel.Left + (panel.Width / 2), 4);
        Assert.Equal(canvas.Height / 2d, panel.Top + (panel.Height / 2), 4);
    }

    [Theory]
    [InlineData(400, 300, 1024, 512)]
    [InlineData(800, 600, 1024, 1024)]
    [InlineData(1600, 900, 1024, 1024)]
    [InlineData(1600, 900, 2048, 2048)]
    public void TextureTierUsesTheSmallestAllowedLongEdge(
        int sourceWidth,
        int sourceHeight,
        int maximumLongEdge,
        int expectedLongEdge)
    {
        Assert.Equal(
            expectedLongEdge,
            OverlayRenderer.SelectTextureLongEdge(
                sourceWidth,
                sourceHeight,
                maximumLongEdge));
    }

    [Fact]
    public void AspectMatchedRenderSizeUsesNoSquarePadding()
    {
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.4f,
            0.2f,
            0.46f,
            default,
            default,
            0);

        var normal = OverlayRenderer.CalculateRenderSize(
            plane,
            OverlayRenderer.DefaultTextureLongEdge);
        var html = OverlayRenderer.CalculateRenderSize(
            plane,
            OverlayRenderer.HtmlTextureLongEdge);

        Assert.Equal((1024, 512), (normal.PixelWidth, normal.PixelHeight));
        Assert.Equal((2048, 1024), (html.PixelWidth, html.PixelHeight));
    }

    [Fact]
    public void HtmlResultUsesNative2048AspectMatchedTexture()
    {
        var renderer = new OverlayRenderer();
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.4f,
            0.2f,
            0.46f,
            default,
            default,
            0);
        var state = new ResultOverlayState();
        var requestId = state.Begin("loading");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9ZQmcAAAAASUVORK5CYII=");
        state.Complete(
            requestId,
            "<html><body>translated</body></html>",
            false,
            ResultContentFormat.Html,
            "translated",
            png);

        var frame = renderer.RenderResults(state.Snapshot()!, plane);

        Assert.Equal(2048, frame.PixelWidth);
        Assert.Equal(1024, frame.PixelHeight);
        Assert.Equal(frame.PixelWidth * frame.PixelHeight * 4, frame.Pixels.Length);
    }

    [Fact]
    public void SelectionHintStaysImmediatelyAboveAndWithinFrameWidth()
    {
        var frame = new System.Windows.Rect(90, 120, 280, 240);

        var hint = OverlayRenderer.CalculateSelectionHintBounds(frame);

        Assert.False(hint.IsEmpty);
        Assert.True(hint.Bottom <= frame.Top);
        Assert.Equal(5, frame.Top - hint.Bottom, 5);
        Assert.True(hint.Width <= frame.Width);
        Assert.True(hint.Left >= 0);
        Assert.True(hint.Right <= OverlayRenderer.Width);
    }

    [Fact]
    public void SelectionHintIsOmittedInsteadOfOverlappingFrame()
    {
        var frame = new System.Windows.Rect(90, 12, 280, 240);

        var hint = OverlayRenderer.CalculateSelectionHintBounds(frame);

        Assert.True(hint.IsEmpty);
    }

    [WpfRenderingFact]
    public void NativeTextBoxScrollCanReturnExactlyToFirstLine()
    {
        var renderer = new OverlayRenderer();
        var state = new ResultOverlayState();
        var requestId = state.Begin("loading");
        state.Complete(
            requestId,
            string.Join('\n', Enumerable.Range(1, 80).Select(index => $"line {index:D2} native scrolling")),
            isError: false);
        var plane = new SpatialSelectionPlane(
            new Vector3f(0, 1.2f, -0.5f),
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.42f,
            0.16f,
            0.42f,
            new NormalizedPoint(0.1f, 0.2f),
            new NormalizedPoint(0.9f, 0.8f),
            0);

        var top = renderer.RenderResults(state.Snapshot()!, plane);
        Assert.True(top.MaximumScrollOffset > 0);
        Assert.True(state.Scroll(top.MaximumScrollOffset, top.MaximumScrollOffset));
        var bottom = renderer.RenderResults(state.Snapshot()!, plane);
        Assert.False(top.Pixels.SequenceEqual(bottom.Pixels));

        Assert.True(state.Scroll(-double.MaxValue, bottom.MaximumScrollOffset));
        var topAgain = renderer.RenderResults(state.Snapshot()!, plane);

        Assert.Equal(0, state.Snapshot()!.ScrollOffset);
        Assert.True(top.Pixels.SequenceEqual(topAgain.Pixels));
    }

    [WpfRenderingFact]
    public void RetainedMarkdownSurfaceCanScrollBackToTheOriginalFrame()
    {
        var renderer = new OverlayRenderer();
        var cacheOwner = new object();
        var state = new ResultOverlayState();
        var markdown = string.Join(
            "\n\n",
            Enumerable.Range(1, 36).Select(index =>
                $"## Section {index:D2}\n\nParagraph {index} with **bold text** and enough content to scroll."));
        var requestId = state.Begin(markdown, ResultContentFormat.Markdown);
        state.Complete(
            requestId,
            markdown,
            isError: false,
            ResultContentFormat.Markdown,
            renderer.ExtractVisibleText(ResultContentFormat.Markdown, markdown));
        var plane = CreateToolbarTestPlane();

        var top = renderer.RenderResults(state.Snapshot()!, plane, cacheOwner: cacheOwner);
        Assert.True(top.MaximumScrollOffset > 0);
        Assert.True(state.Scroll(top.MaximumScrollOffset, top.MaximumScrollOffset));
        var bottom = renderer.RenderResults(state.Snapshot()!, plane, cacheOwner: cacheOwner);
        Assert.False(top.Pixels.SequenceEqual(bottom.Pixels));

        Assert.True(state.Scroll(-double.MaxValue, bottom.MaximumScrollOffset));
        var topAgain = renderer.RenderResults(state.Snapshot()!, plane, cacheOwner: cacheOwner);

        Assert.Equal(0, state.Snapshot()!.ScrollOffset);
        Assert.True(top.Pixels.SequenceEqual(topAgain.Pixels));
    }

    [WpfRenderingFact]
    public void DirectResultFrameReportsTheExactRoundedTextureDimensions()
    {
        var renderer = new OverlayRenderer();
        var state = new ResultOverlayState();
        var requestId = state.Begin("# Exact size", ResultContentFormat.Markdown);
        state.Complete(
            requestId,
            "# Exact size",
            isError: false,
            ResultContentFormat.Markdown,
            "Exact size");
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.42f,
            0.28f,
            0.42f,
            default,
            default,
            0);

        var frame = renderer.RenderResults(state.Snapshot()!, plane, cacheOwner: new object());

        Assert.Equal(1024, frame.PixelWidth);
        Assert.Equal(683, frame.PixelHeight);
        Assert.Equal(frame.PixelWidth * frame.PixelHeight * 4, frame.Pixels.Length);
    }

    [WpfRenderingFact]
    public void RetainedChatSurfaceUpdatesOnlyTheStreamingConversationView()
    {
        var renderer = new OverlayRenderer();
        var cacheOwner = new object();
        var history = Array.Empty<AssistantConversationTurn>();
        var state = new ResultOverlayState();
        var initialChat = new AssistantConversationView(history, "Continue.", "First segment");
        var requestId = state.Begin(
            initialChat.PendingAnswer!,
            ResultContentFormat.Markdown,
            initialChat);
        var plane = CreateToolbarTestPlane();

        var first = renderer.RenderResults(state.Snapshot()!, plane, cacheOwner: cacheOwner);
        var updatedChat = new AssistantConversationView(
            history,
            "Continue.",
            "First segment followed by a second streaming segment with more detail.");
        Assert.True(state.Update(requestId, updatedChat.PendingAnswer!, updatedChat));
        var second = renderer.RenderResults(state.Snapshot()!, plane, cacheOwner: cacheOwner);

        Assert.False(first.Pixels.SequenceEqual(second.Pixels));
        Assert.Equal(first.PixelWidth, second.PixelWidth);
        Assert.Equal(first.PixelHeight, second.PixelHeight);
    }

    [WpfRenderingFact]
    public void HighlightingDoesNotToggleRendererState()
    {
        var renderer = new OverlayRenderer();
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.4f,
            0.3f,
            0.46f,
            default,
            default,
            0);
        var state = new ResultOverlayState();
        var requestId = state.Begin("highlight test");
        state.Complete(requestId, "highlight test", false);

        var normal = renderer.RenderResults(state.Snapshot()!, plane).Pixels;
        var highlighted = renderer.RenderResults(
            state.Snapshot()!,
            plane,
            highlighted: true).Pixels;
        var normalAgain = renderer.RenderResults(state.Snapshot()!, plane).Pixels;

        Assert.False(normal.SequenceEqual(highlighted));
        Assert.True(normal.SequenceEqual(normalAgain));
    }

    [Fact]
    public void DecorationsDoNotChangeScrollableTextLayout()
    {
        var renderer = new OverlayRenderer();
        var plane = CreateToolbarTestPlane();
        var state = new ResultOverlayState();
        var requestId = state.Begin("layout test");
        state.Complete(
            requestId,
            string.Join('\n', Enumerable.Range(1, 80).Select(index => $"line {index:D2}")),
            false);

        var normal = renderer.RenderResults(state.Snapshot()!, plane);
        var highlighted = renderer.RenderResults(
            state.Snapshot()!,
            plane,
            highlighted: true);
        var withToolbar = renderer.RenderResults(
            state.Snapshot()!,
            plane,
            highlighted: true,
            showToolbar: true);

        Assert.True(normal.MaximumScrollOffset > 0);
        Assert.Equal(normal.MaximumScrollOffset, highlighted.MaximumScrollOffset, 5);
        Assert.Equal(normal.MaximumScrollOffset, withToolbar.MaximumScrollOffset, 5);
    }

    [WpfRenderingFact]
    public void ToolbarIsOnlyCompositedWhenOverlayIsHeld()
    {
        var renderer = new OverlayRenderer();
        var plane = CreateToolbarTestPlane();
        var state = new ResultOverlayState();
        var requestId = state.Begin("toolbar test");
        state.Complete(requestId, "toolbar test", false);

        var hidden = renderer.RenderResults(state.Snapshot()!, plane).Pixels;
        var visible = renderer.RenderResults(
            state.Snapshot()!,
            plane,
            showToolbar: true).Pixels;

        Assert.False(hidden.SequenceEqual(visible));
    }

    [Fact]
    public void ResultToolbarExposesSendAndCloseButtons()
    {
        var plane = CreateToolbarTestPlane();
        var send = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Result,
            plane,
            OverlayToolbarAction.Send);
        var close = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Result,
            plane,
            OverlayToolbarAction.Close);

        Assert.False(send.IsEmpty);
        Assert.False(close.IsEmpty);
        Assert.Equal(
            OverlayToolbarAction.Send,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Result,
                plane,
                TexturePointAt(plane, send)));
        Assert.Equal(
            OverlayToolbarAction.Close,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Result,
                plane,
                TexturePointAt(plane, close)));
    }

    [Fact]
    public void CaptureToolbarExposesTranslateRecordAndCloseButtons()
    {
        var plane = CreateToolbarTestPlane();
        var translate = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.Translate);
        var layoutTranslate = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.LayoutTranslate);
        var customCommand = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.CustomCommand);
        var close = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.Close);

        Assert.Equal(
            [
                OverlayToolbarAction.Translate,
                OverlayToolbarAction.LayoutTranslate,
                OverlayToolbarAction.Close,
                OverlayToolbarAction.CustomCommand
            ],
            OverlayToolbarLayout.Actions(InteractiveOverlayKind.Capture));
        Assert.Equal(
            OverlayToolbarAction.Translate,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Capture,
                plane,
                TexturePointAt(plane, translate)));
        Assert.Equal(
            OverlayToolbarAction.LayoutTranslate,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Capture,
                plane,
                TexturePointAt(plane, layoutTranslate)));
        Assert.Equal(
            OverlayToolbarAction.CustomCommand,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Capture,
                plane,
                TexturePointAt(plane, customCommand)));
        Assert.Equal(
            OverlayToolbarAction.Close,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Capture,
                plane,
                TexturePointAt(plane, close)));
        Assert.True(
            OverlayToolbarLayout.CalculateButtonBounds(
                InteractiveOverlayKind.Capture,
                plane,
                OverlayToolbarAction.Send).IsEmpty);
    }

    [Fact]
    public void GenericWindowToolbarOnlyExposesCloseButton()
    {
        Assert.Equal(
            [OverlayToolbarAction.Close],
            OverlayToolbarLayout.Actions(InteractiveOverlayKind.Window));
    }

    [Fact]
    public void ToolbarHasOuterInsetAndGapsBetweenButtons()
    {
        var plane = CreateToolbarTestPlane();
        var panel = OverlayRenderer.CalculateResultPanel(plane);
        var toolbar = OverlayToolbarLayout.CalculateBounds(InteractiveOverlayKind.Capture, plane);
        var translate = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.Translate);
        var layoutTranslate = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.LayoutTranslate);
        var customCommand = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.CustomCommand);
        var close = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.Close);

        Assert.True(toolbar.Top > panel.Top);
        Assert.True(toolbar.Right < panel.Right);
        Assert.Equal(OverlayToolbarLayout.ButtonGap, layoutTranslate.Left - translate.Right, 3);
        Assert.Equal(OverlayToolbarLayout.ButtonGap, close.Left - layoutTranslate.Right, 3);
        Assert.True(customCommand.Top > toolbar.Bottom);
        Assert.True(customCommand.Right < panel.Right);
    }

    [Fact]
    public void ToolbarMirrorsToTheSideOppositeTheHoldingHand()
    {
        var plane = CreateToolbarTestPlane();
        var leftToolbar = OverlayToolbarLayout.CalculateBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarSide.Left);
        var rightToolbar = OverlayToolbarLayout.CalculateBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarSide.Right);
        var leftVoice = OverlayToolbarLayout.CalculateVoiceBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarSide.Left);
        var rightVoice = OverlayToolbarLayout.CalculateVoiceBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarSide.Right);

        Assert.True(leftToolbar.Left < rightToolbar.Left);
        Assert.True(leftVoice.Left < rightVoice.Left);
        Assert.Equal(
            [
                OverlayToolbarAction.Close,
                OverlayToolbarAction.LayoutTranslate,
                OverlayToolbarAction.Translate,
                OverlayToolbarAction.CustomCommand
            ],
            OverlayToolbarLayout.Actions(
                InteractiveOverlayKind.Capture,
                OverlayToolbarSide.Left));
        Assert.Equal(
            OverlayToolbarSide.Right,
            OverlayToolbarPlacement.OppositeHoldingHand(
                Valve.VR.ETrackedControllerRole.LeftHand));
        Assert.Equal(
            OverlayToolbarSide.Left,
            OverlayToolbarPlacement.OppositeHoldingHand(
                Valve.VR.ETrackedControllerRole.RightHand));

        var mirroredTranslate = OverlayToolbarLayout.CalculateButtonBounds(
            InteractiveOverlayKind.Capture,
            plane,
            OverlayToolbarAction.Translate,
            OverlayToolbarSide.Left);
        Assert.Equal(
            OverlayToolbarAction.Translate,
            OverlayToolbarLayout.HitTest(
                InteractiveOverlayKind.Capture,
                plane,
                TexturePointAt(plane, mirroredTranslate),
                OverlayToolbarSide.Left));
    }

    [Fact]
    public void ToolbarIconsUseTheManagerMaterialGeometrySource()
    {
        Assert.Same(
            MaterialIconPaths.Close,
            WpfOverlayToolbar.IconGeometry(OverlayToolbarAction.Close));
        Assert.Same(
            MaterialIconPaths.Send,
            WpfOverlayToolbar.IconGeometry(OverlayToolbarAction.Send));
        Assert.Same(
            MaterialIconPaths.Translate,
            WpfOverlayToolbar.IconGeometry(OverlayToolbarAction.Translate));
        Assert.Same(
            MaterialIconPaths.ViewDashboardOutline,
            WpfOverlayToolbar.IconGeometry(OverlayToolbarAction.LayoutTranslate));
        Assert.Same(
            MaterialIconPaths.Microphone,
            WpfOverlayToolbar.IconGeometry(OverlayToolbarAction.CustomCommand));
        Assert.False(MaterialIconPaths.Close.Bounds.IsEmpty);
        Assert.False(MaterialIconPaths.Send.Bounds.IsEmpty);
        Assert.False(MaterialIconPaths.Translate.Bounds.IsEmpty);
        Assert.False(MaterialIconPaths.ViewDashboardOutline.Bounds.IsEmpty);
        Assert.False(MaterialIconPaths.Microphone.Bounds.IsEmpty);
    }

    [Fact]
    public void ToolbarIconsPreserveTheMaterialDesignCanvasForCentering()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var action in Enum.GetValues<OverlayToolbarAction>())
                {
                    var host = WpfOverlayToolbar.CreateIconView(action, out var icon);
                    var canvas = Assert.IsType<System.Windows.Controls.Canvas>(host.Child);

                    Assert.Equal(24, canvas.Width);
                    Assert.Equal(24, canvas.Height);
                    Assert.Equal(24, icon.Width);
                    Assert.Equal(24, icon.Height);
                    Assert.Equal(System.Windows.Media.Stretch.None, icon.Stretch);
                    Assert.Equal(System.Windows.HorizontalAlignment.Center, host.HorizontalAlignment);
                    Assert.Equal(System.Windows.VerticalAlignment.Center, host.VerticalAlignment);
                    Assert.True(icon.Data.Bounds.Left >= 0);
                    Assert.True(icon.Data.Bounds.Top >= 0);
                    Assert.True(icon.Data.Bounds.Right <= 24);
                    Assert.True(icon.Data.Bounds.Bottom <= 24);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [WpfRenderingFact]
    public void PointerIsCompositedIntoCaptureTexture()
    {
        var renderer = new OverlayRenderer();
        var plane = new SpatialSelectionPlane(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.4f,
            0.3f,
            0.46f,
            default,
            default,
            0);
        var state = new ResultOverlayState();
        var requestId = state.Begin("pointer test");
        state.Complete(requestId, "pointer test", false);

        var plain = renderer.RenderResults(state.Snapshot()!, plane).Pixels;
        var pointer = new OverlayPointerVisual(
            Valve.VR.ETrackedControllerRole.RightHand,
            new NormalizedPoint(0.5f, 0.5f),
            new NormalizedPoint(0.5f, 0.5f),
            false);
        var withPointer = renderer.RenderResults(state.Snapshot()!, plane, pointer: pointer).Pixels;

        Assert.False(plain.SequenceEqual(withPointer));
    }

    [WpfRenderingFact]
    public void InteractiveDecorationsHaveDedicatedTransparentTextures()
    {
        var renderer = new OverlayRenderer();
        var plane = CreateToolbarTestPlane();

        var emptyChrome = renderer.RenderChrome(
            InteractiveOverlayKind.Result,
            plane,
            showToolbar: false,
            pointer: null,
            isCommandRecording: false);
        var toolbarChrome = renderer.RenderChrome(
            InteractiveOverlayKind.Result,
            plane,
            showToolbar: true,
            pointer: null,
            isCommandRecording: false);
        var pointer = renderer.RenderPointer(isPressed: false);
        var ray = renderer.RenderPointerRay();
        var progress = renderer.RenderCloseHoldProgress(plane, 0.5);

        Assert.False(HasVisiblePixel(emptyChrome.Pixels));
        Assert.True(HasVisiblePixel(toolbarChrome.Pixels));
        Assert.Equal(OverlayRenderer.PointerTextureSize, pointer.PixelWidth);
        Assert.Equal(OverlayRenderer.PointerTextureSize, pointer.PixelHeight);
        Assert.Equal(OverlayRenderer.PointerRayTextureWidth, ray.PixelWidth);
        Assert.Equal(OverlayRenderer.PointerRayTextureHeight, ray.PixelHeight);
        Assert.Equal(OverlayRenderer.ProgressTextureSize, progress.PixelWidth);
        Assert.Equal(OverlayRenderer.ProgressTextureSize, progress.PixelHeight);
        Assert.True(HasVisiblePixel(pointer.Pixels));
        Assert.True(HasVisiblePixel(ray.Pixels));
        Assert.True(HasVisiblePixel(progress.Pixels));
    }

    [Fact]
    public void PbgraConversionReusesTheSameBufferAndHandlesAlphaFastPaths()
    {
        byte[] pixels =
        [
            10, 20, 30, 255,
            25, 50, 100, 128,
            80, 70, 60, 0
        ];

        OverlayRenderer.ConvertPbgraToRgbaInPlace(pixels);

        Assert.Equal(
            new byte[]
            {
                30, 20, 10, 255,
                199, 99, 49, 128,
                0, 0, 0, 0
            },
            pixels);
    }

    [WpfRenderingFact]
    public void ToolbarReusesBitmapUntilItsVisualStateChanges()
    {
        System.Windows.Media.Imaging.BitmapSource? first = null;
        System.Windows.Media.Imaging.BitmapSource? same = null;
        System.Windows.Media.Imaging.BitmapSource? changed = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var toolbar = new WpfOverlayToolbar(InteractiveOverlayKind.Result);
                first = toolbar.Render(128, 36, OverlayToolbarAction.Send, false, 2);
                same = toolbar.Render(128, 36, OverlayToolbarAction.Send, false, 2);
                changed = toolbar.Render(128, 36, OverlayToolbarAction.Close, false, 2);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.Same(first, same);
        Assert.NotSame(first, changed);
    }

    [WpfRenderingFact]
    public void ToolbarButtonsAreSeparatedByTransparentPixels()
    {
        System.Windows.Media.Imaging.BitmapSource? bitmap = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var toolbar = new WpfOverlayToolbar(InteractiveOverlayKind.Result);
                bitmap = toolbar.Render(76, 40, null, false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.NotNull(bitmap);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        Assert.True(AlphaAt(pixels, stride, 20, 20) > 0);
        Assert.Equal(0, AlphaAt(pixels, stride, 38, 20));
        Assert.True(AlphaAt(pixels, stride, 56, 20) > 0);
    }

    [WpfRenderingFact]
    public void VoiceButtonRerendersWhenRecordingStateChanges()
    {
        System.Windows.Media.Imaging.BitmapSource? idle = null;
        System.Windows.Media.Imaging.BitmapSource? recording = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var toolbar = new WpfOverlayToolbar([OverlayToolbarAction.CustomCommand]);
                idle = toolbar.Render(40, 40, null, false, 2, isCommandRecording: false);
                recording = toolbar.Render(40, 40, null, false, 2, isCommandRecording: true);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
        Assert.NotSame(idle, recording);
    }

    private static SpatialSelectionPlane CreateToolbarTestPlane() =>
        new(
            default,
            new Vector3f(1, 0, 0),
            new Vector3f(0, 1, 0),
            new Vector3f(0, 0, 1),
            0.4f,
            0.3f,
            0.46f,
            default,
            default,
            0);

    private static byte AlphaAt(byte[] pixels, int stride, int x, int y) =>
        pixels[(y * stride) + (x * 4) + 3];

    private static bool HasVisiblePixel(byte[] rgba)
    {
        for (var index = 3; index < rgba.Length; index += 4)
        {
            if (rgba[index] != 0)
            {
                return true;
            }
        }

        return false;
    }



    private static NormalizedPoint TexturePointAt(
        SpatialSelectionPlane plane,
        System.Windows.Rect bounds)
    {
        var canvas = OverlayRenderer.CalculateLogicalCanvasSize(plane);
        return new NormalizedPoint(
            (float)((bounds.Left + (bounds.Width / 2)) / canvas.Width),
            (float)((bounds.Top + (bounds.Height / 2)) / canvas.Height));
    }
}
