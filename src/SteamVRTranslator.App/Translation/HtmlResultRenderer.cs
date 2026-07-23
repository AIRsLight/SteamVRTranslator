using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Translation;

internal sealed class HtmlResultRenderer : IAsyncDisposable
{
    private const int MaximumViewportDimension = 2048;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Window? _window;
    private WebView2? _webView;
    private bool _disposed;

    public HtmlResultRenderer(AppLog log)
    {
        _log = log;
        _thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "SteamVRTranslator.HtmlRenderer.STA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public async Task<HtmlRenderResult> RenderAsync(
        string html,
        int sourcePixelWidth,
        int sourcePixelHeight,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var prepared = ResultContentFormatter.PrepareHtml(html);
        var viewport = CalculateViewport(sourcePixelWidth, sourcePixelHeight);
        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            var dispatcher = await _dispatcherReady.Task.WaitAsync(cancellationToken);
            var operation = dispatcher.InvokeAsync(
                () => RenderOnDispatcherAsync(prepared, viewport, cancellationToken),
                DispatcherPriority.Normal,
                cancellationToken);
            var renderTask = await operation.Task;
            return await renderTask.WaitAsync(cancellationToken);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await _renderGate.WaitAsync();
        try
        {
            if (_dispatcherReady.Task.IsCompletedSuccessfully)
            {
                var dispatcher = await _dispatcherReady.Task;
                await dispatcher.InvokeAsync(CloseBrowser, DispatcherPriority.Send);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
        }
        finally
        {
            _renderGate.Release();
        }

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(3));
        }
        _renderGate.Dispose();
    }

    internal static HtmlViewport CalculateViewport(int sourcePixelWidth, int sourcePixelHeight)
    {
        var width = Math.Max(1, sourcePixelWidth);
        var height = Math.Max(1, sourcePixelHeight);
        var scale = MaximumViewportDimension / (double)Math.Max(width, height);
        return new HtmlViewport(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private void RunDispatcher()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcherReady.TrySetResult(dispatcher);
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            _dispatcherReady.TrySetException(exception);
        }
    }

    private async Task<HtmlRenderResult> RenderOnDispatcherAsync(
        PreparedHtmlDocument prepared,
        HtmlViewport viewport,
        CancellationToken cancellationToken)
    {
        await EnsureBrowserAsync();
        var webView = _webView ?? throw new InvalidOperationException("WebView2 未初始化。");
        var window = _window ?? throw new InvalidOperationException("WebView2 承载窗口未初始化。");
        webView.ZoomFactor = 1;
        window.Width = viewport.Width;
        window.Height = viewport.Height;
        webView.Width = viewport.Width;
        webView.Height = viewport.Height;
        window.UpdateLayout();
        webView.UpdateLayout();

        var navigation = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
            navigation.TrySetResult(args);

        webView.NavigationCompleted += NavigationCompleted;
        try
        {
            webView.NavigateToString(prepared.Html);
            var completed = await navigation.Task
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (!completed.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"WebView2 HTML 导航失败：{completed.WebErrorStatus}。");
            }

            await Task.Delay(50, cancellationToken);

            using var output = new MemoryStream();
            await webView.CoreWebView2.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png,
                output);
            var png = NormalizePngSize(output.ToArray(), viewport.Width, viewport.Height);
            _log.Info(
                $"[html] WebView2 排版结果已渲染：视口={viewport.Width}x{viewport.Height}，" +
                $"PNG={png.Length:N0} bytes，可见字符={prepared.VisibleText.Length}。");
            return new HtmlRenderResult(
                png,
                prepared.VisibleText);
        }
        finally
        {
            webView.NavigationCompleted -= NavigationCompleted;
            CloseBrowser();
            _log.Info("[html] 排版结果已静态化，WebView2 文档与 DOM 已释放。");
        }
    }

    private async Task EnsureBrowserAsync()
    {
        if (_webView?.CoreWebView2 is not null)
        {
            return;
        }

        var webView = _webView ?? new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ZoomFactor = 1,
            UseLayoutRounding = true
        };
        var window = _window ?? new Window
        {
            Title = "SteamVR Translator HTML Renderer",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -32000,
            Top = -32000,
            Opacity = 0.01,
            Width = 64,
            Height = 64,
            Content = webView
        };
        _webView = webView;
        _window = window;
        if (!window.IsVisible)
        {
            window.Show();
        }

        var userDataFolder = ApplicationDataPaths.WebView2Directory;
        Directory.CreateDirectory(userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder);
        await webView.EnsureCoreWebView2Async(environment);

        var core = webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsScriptEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) =>
            args.State = CoreWebView2PermissionState.Deny;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (args.Request.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                args.Request.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                args.Request.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                args.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(),
                    403,
                    "Blocked",
                    "Content-Type: text/plain");
            }
        };
        _log.Info("[html] WebView2 离屏渲染器已初始化。");
    }

    private static byte[] NormalizePngSize(byte[] png, int width, int height)
    {
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth == width && frame.PixelHeight == height)
        {
            return png;
        }

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(frame, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private void CloseBrowser()
    {
        _webView?.Dispose();
        _webView = null;
        _window?.Close();
        _window = null;
    }
}

internal readonly record struct HtmlViewport(int Width, int Height);

internal sealed record HtmlRenderResult(
    byte[] RenderedImage,
    string VisibleText);
