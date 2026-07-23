using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SteamVRTranslator.App;
using SteamVRTranslator.App.AndroidMirror;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.App.Subtitles;
using SteamVRTranslator.App.Translation;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.OverlayBenchmark;

internal static class Program
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint periodMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint periodMilliseconds);

    private static readonly TimeSpan PhaseDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InvalidationInterval = TimeSpan.FromMilliseconds(5);
    private static readonly string[] ControlInteractionSequence =
    [
        "SettingsMenuButton",
        "RightEyeButton",
        "HomeButton",
        "VoiceMenuButton",
        "VoiceEnabledToggle",
        "HomeButton"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(value => string.Equals(
                value,
                "--install-android-runtime",
                StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                return InstallAndroidRuntimeAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 2;
            }
        }

        if (args.Any(value => string.Equals(
                value,
                "--android-stream",
                StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                return RunAndroidStreamAsync(args, ResolveOutputPath(args))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 2;
            }
        }

        var exitCode = 2;
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        application.Startup += (_, _) => application.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                exitCode = await RunAsync(args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                exitCode = 2;
            }
            finally
            {
                application.Shutdown(exitCode);
            }
        });
        return application.Run();
    }

    private static async Task<int> InstallAndroidRuntimeAsync()
    {
        using var runtime = new AndroidMirrorRuntimeService();
        runtime.ProgressChanged += (_, progress) => Console.WriteLine(
            $"{progress.Message} {progress.BytesDownloaded}/{progress.TotalBytes}");
        await runtime.DownloadAsync();
        Console.WriteLine($"scrcpy={runtime.RuntimeDirectory}");
        Console.WriteLine($"ffmpeg={runtime.FfmpegRuntimeDirectory}");
        return runtime.IsInstalled && runtime.IsHardwareDecoderInstalled ? 0 : 1;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var outputPath = ResolveOutputPath(args);
        if (args.Any(value => string.Equals(value, "--android-mirror", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunAndroidMirrorAsync(args, outputPath);
        }
        if (args.Any(value => string.Equals(value, "--realistic", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunRealisticStressAsync(args, outputPath);
        }
        if (args.Any(value => string.Equals(value, "--stress", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunStressAsync(args, outputPath);
        }

        var dataDirectory = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            "runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var configuration = new AppConfiguration();
        var log = new AppLog(dataDirectory);
        await using var runtime = new SteamVrTranslationRuntime(configuration, log);
        var frames = new ConcurrentQueue<WpfOverlayFrameRenderedEventArgs>();
        var pointerEvents = new ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs>();
        runtime.WpfOverlayFrameRendered += (_, frame) => frames.Enqueue(frame);
        runtime.DiagnosticWpfPointerProcessed += (_, pointer) => pointerEvents.Enqueue(pointer);

        var controlPanel = new VrControlPanelWindow(new VrControlPanelState(
            VoiceEnabled: true,
            TranslationEnabled: true,
            SendImmediately: true,
            DisplayMode: VoiceTranslationDisplayModes.OriginalThenTranslation,
            TargetLanguage: "zh-CN",
            ChunkIntervalMilliseconds: 1000,
            CaptureEye: "left-eye"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        var history = new SubtitleHistoryViewModel(configuration.Subtitles);
        var entry = history.Add(
            0,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            "SteamVR Null Driver overlay rendering benchmark");
        entry.TranslatedText = "SteamVR Null Driver 叠加层渲染基准";
        entry.IsTranslating = false;
        var subtitleWindow = new SubtitleHistoryWindow(history)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        using var androidMirrorRuntime = new AndroidMirrorRuntimeService();
        await using var androidMirrorSession = new ScrcpyAndroidSession(androidMirrorRuntime, log);
        var androidMirrorWindow = new AndroidMirrorWindow(androidMirrorSession)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        androidMirrorWindow.ConfigureForSource(1080, 2400);
        controlPanel.Show();
        subtitleWindow.Show();
        androidMirrorWindow.Show();
        controlPanel.UpdateLayout();
        subtitleWindow.UpdateLayout();
        androidMirrorWindow.UpdateLayout();

        await runtime.StartAsyncForDiagnostics();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var controlId = await runtime.ShowWindowAsync(
            controlPanel,
            new WpfSpatialOverlayOptions
            {
                Name = "Benchmark Control Panel",
                WidthMeters = 0.25f,
                Placement = WpfSpatialOverlayPlacement.Head,
                CanGrab = true,
                ShowToolbarWhenGrabbed = false,
                MaximumFramesPerSecond = 60
            },
            timeout.Token);
        var subtitleId = await runtime.ShowWindowAsync(
            subtitleWindow,
            MainWindow.CreateSubtitleOverlayOptions(
                configuration.Subtitles,
                WpfSpatialOverlayPlacement.Head),
            timeout.Token);
        var androidMirrorId = await runtime.ShowWindowAsync(
            androidMirrorWindow,
            new WpfSpatialOverlayOptions
            {
                Name = "Benchmark Android Mirror",
                WidthMeters = (float)androidMirrorWindow.RecommendedWidthMeters,
                Placement = WpfSpatialOverlayPlacement.Head,
                CanGrab = true,
                ShowToolbarWhenGrabbed = false,
                MaximumFramesPerSecond = 60
            },
            timeout.Token);

        await Task.Delay(750, timeout.Token);
        var openVrPointerChecks = (await VerifyOpenVrGuiPointerAsync(
            runtime,
            controlPanel,
            controlId,
            pointerEvents,
            Path.GetDirectoryName(outputPath)!,
            timeout.Token)).ToList();
        openVrPointerChecks.AddRange(await VerifyAndroidMirrorOpenVrPointerAsync(
            runtime,
            androidMirrorWindow,
            androidMirrorId,
            pointerEvents,
            Path.GetDirectoryName(outputPath)!,
            timeout.Token));
        var openVrPointerLeaveCleared = openVrPointerChecks.LastOrDefault()?.LeftWindow == true &&
                                        openVrPointerChecks.Last().HoveredControlName is null;
        await Task.Delay(250, timeout.Token);
        Drain(frames);
        await Task.Delay(PhaseDuration, timeout.Token);
        var idleFrames = Drain(frames);
        var controlPhase = await MeasurePhaseAsync(runtime, frames, controlId, timeout.Token);
        var subtitlePhase = await MeasurePhaseAsync(runtime, frames, subtitleId, timeout.Token);
        var combinedPhase = await MeasureCombinedPhaseAsync(
            runtime,
            frames,
            controlId,
            subtitleId,
            timeout.Token);

        await runtime.CloseWindowAsync(controlId, timeout.Token);
        await runtime.CloseWindowAsync(subtitleId, timeout.Token);
        await runtime.CloseWindowAsync(androidMirrorId, timeout.Token);
        controlPanel.Close();
        subtitleWindow.ClosePermanently();
        androidMirrorWindow.Close();

        var report = new OverlayBenchmarkReport(
            TargetFramesPerSecond: 60,
            IdleContentUploads: idleFrames.Count,
            ControlPanel: Summarize(controlPhase, controlId),
            SubtitleWindow: Summarize(subtitlePhase, subtitleId),
            CombinedControlPanel: Summarize(combinedPhase, controlId),
            CombinedSubtitleWindow: Summarize(combinedPhase, subtitleId),
            CombinedP95FrameMilliseconds: Percentile(
                combinedPhase
                    .GroupBy(frame => frame.RenderedAt)
                    .Select(group => group.Sum(frame => frame.TotalDuration.TotalMilliseconds)),
                0.95),
            OpenVrPointerChecks: openVrPointerChecks,
            OpenVrPointerLeaveCleared: openVrPointerLeaveCleared);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var passed = report.IdleContentUploads == 0 &&
                     report.ControlPanel.EffectiveFramesPerSecond >= 59.5 &&
                     report.SubtitleWindow.EffectiveFramesPerSecond >= 59.5 &&
                     report.CombinedControlPanel.EffectiveFramesPerSecond >= 59.5 &&
                     report.CombinedSubtitleWindow.EffectiveFramesPerSecond >= 59.5 &&
                     report.ControlPanel.P95TotalMilliseconds <= 1000d / 60d &&
                     report.SubtitleWindow.P95TotalMilliseconds <= 1000d / 60d &&
                     report.CombinedControlPanel.P95TotalMilliseconds <= 1000d / 60d &&
                     report.CombinedSubtitleWindow.P95TotalMilliseconds <= 1000d / 60d &&
                     report.CombinedP95FrameMilliseconds <= 1000d / 60d &&
                     report.OpenVrPointerChecks.All(check => check.Passed) &&
                     report.OpenVrPointerLeaveCleared;
        return passed ? 0 : 1;
    }

    private static async Task<IReadOnlyList<OpenVrPointerCheckReport>> VerifyOpenVrGuiPointerAsync(
        SteamVrTranslationRuntime runtime,
        VrControlPanelWindow window,
        long overlayId,
        ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs> events,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        Drain(events);
        var reports = new List<OpenVrPointerCheckReport>();
        foreach (var controlName in new[]
                 {
                     "CaptureMenuButton",
                     "VoiceMenuButton",
                     "SubtitleMenuButton",
                     "SettingsMenuButton"
                 })
        {
            var requested = FindControlCenter(window, controlName);
            var requestId = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
                overlayId,
                requested);
            var completed = await WaitForPointerAsync(events, requestId, cancellationToken);
            var resolved = completed.ResolvedTexturePoint;
            var errorPixels = resolved is { } point
                ? Math.Sqrt(
                    Math.Pow((point.X - requested.X) * 840, 2) +
                    Math.Pow((point.Y - requested.Y) * 560, 2))
                : 1_000_000_000d;
            reports.Add(new OpenVrPointerCheckReport(
                controlName,
                requested,
                resolved,
                completed.HoveredControlName,
                errorPixels,
                completed.UsedOpenVrIntersection &&
                string.Equals(completed.HoveredControlName, controlName, StringComparison.Ordinal) &&
                errorPixels <= 2.0,
                completed.LeftWindow));
        }

        SaveWindowVisual(window, Path.Combine(artifactDirectory, "openvr-hover-control-panel.png"));

        var edgeRequestId = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
            overlayId,
            new NormalizedPoint(0.001f, 0.999f));
        var edge = await WaitForPointerAsync(events, edgeRequestId, cancellationToken);
        var edgeResolved = edge.ResolvedTexturePoint;
        reports.Add(new OpenVrPointerCheckReport(
            "ExactBottomLeftInside",
            edge.RequestedTexturePoint,
            edgeResolved,
            edge.HoveredControlName,
            edgeResolved is { } edgePoint
                ? Math.Sqrt(
                    Math.Pow((edgePoint.X - edge.RequestedTexturePoint.X) * 840, 2) +
                    Math.Pow((edgePoint.Y - edge.RequestedTexturePoint.Y) * 560, 2))
                : 1_000_000_000d,
            edge.UsedOpenVrIntersection &&
            edgeResolved is not null &&
            Math.Abs(edgeResolved.Value.X - edge.RequestedTexturePoint.X) <= 0.002f &&
            Math.Abs(edgeResolved.Value.Y - edge.RequestedTexturePoint.Y) <= 0.002f,
            edge.LeftWindow));

        var outsideRequestId = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
            overlayId,
            new NormalizedPoint(-0.001f, 1.001f));
        var outside = await WaitForPointerAsync(events, outsideRequestId, cancellationToken);
        reports.Add(new OpenVrPointerCheckReport(
            "PointerOutsideHasNoSnap",
            outside.RequestedTexturePoint,
            outside.ResolvedTexturePoint,
            outside.HoveredControlName,
            0,
            outside.UsedOpenVrIntersection && outside.ResolvedTexturePoint is null,
            outside.LeftWindow));

        var leaveRequestId = runtime.LeaveWindowPointerForDiagnostics(overlayId);
        var leave = await WaitForPointerAsync(events, leaveRequestId, cancellationToken);
        reports.Add(new OpenVrPointerCheckReport(
            "PointerLeave",
            leave.RequestedTexturePoint,
            leave.ResolvedTexturePoint,
            leave.HoveredControlName,
            0,
            leave.LeftWindow && leave.HoveredControlName is null,
            leave.LeftWindow));
        return reports;
    }

    private static async Task<IReadOnlyList<OpenVrPointerCheckReport>> VerifyAndroidMirrorOpenVrPointerAsync(
        SteamVrTranslationRuntime runtime,
        AndroidMirrorWindow window,
        long overlayId,
        ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs> events,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        Drain(events);
        var reports = new List<OpenVrPointerCheckReport>();
        foreach (var controlName in new[] { "BackButton", "HomeButton", "RecentAppsButton" })
        {
            var requested = FindControlCenter(window, controlName);
            var requestId = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(overlayId, requested);
            var completed = await WaitForPointerAsync(events, requestId, cancellationToken);
            var resolved = completed.ResolvedTexturePoint;
            var errorPixels = resolved is { } point
                ? Math.Sqrt(
                    Math.Pow((point.X - requested.X) * window.ActualWidth, 2) +
                    Math.Pow((point.Y - requested.Y) * window.ActualHeight, 2))
                : 1_000_000_000d;
            reports.Add(new OpenVrPointerCheckReport(
                $"Android.{controlName}",
                requested,
                resolved,
                completed.HoveredControlName,
                errorPixels,
                completed.UsedOpenVrIntersection &&
                string.Equals(completed.HoveredControlName, controlName, StringComparison.Ordinal) &&
                errorPixels <= 2.0,
                completed.LeftWindow));
        }

        SaveWindowVisual(window, Path.Combine(artifactDirectory, "openvr-hover-android-mirror.png"));
        var leaveRequestId = runtime.LeaveWindowPointerForDiagnostics(overlayId);
        var leave = await WaitForPointerAsync(events, leaveRequestId, cancellationToken);
        reports.Add(new OpenVrPointerCheckReport(
            "Android.PointerLeave",
            leave.RequestedTexturePoint,
            leave.ResolvedTexturePoint,
            leave.HoveredControlName,
            0,
            leave.LeftWindow && leave.HoveredControlName is null,
            leave.LeftWindow));
        return reports;
    }

    private static NormalizedPoint FindControlCenter(Window window, string controlName)
    {
        var root = window.Content as FrameworkElement
                   ?? throw new InvalidOperationException("Control panel root is unavailable.");
        var control = window.FindName(controlName) as FrameworkElement
                      ?? throw new InvalidOperationException($"Control '{controlName}' is unavailable.");
        window.UpdateLayout();
        var center = control.TranslatePoint(
            new Point(control.ActualWidth / 2d, control.ActualHeight / 2d),
            root);
        return new NormalizedPoint(
            (float)(center.X / root.ActualWidth),
            (float)(center.Y / root.ActualHeight));
    }

    private static async Task<DiagnosticWpfPointerProcessedEventArgs> WaitForPointerAsync(
        ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs> events,
        long requestId,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (events.TryDequeue(out var completed))
            {
                if (completed.RequestId == requestId)
                {
                    return completed;
                }
            }
            await Task.Delay(5, cancellationToken);
        }
        throw new TimeoutException($"OpenVR pointer request {requestId} did not complete.");
    }

    private static void SaveWindowVisual(Window window, string path)
    {
        var visual = window.Content as Visual ?? window;
        var width = Math.Max(1, (int)Math.Round(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Round(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task<int> RunAndroidStreamAsync(string[] args, string outputPath)
    {
        var deviceSerial = ResolveStringArgument(args, "--device=");
        var phaseSeconds = ResolveIntArgument(args, "--phase-seconds=", 8, 4, 30);
        var requestedSizes = ResolveIntListArgument(args, "--sizes=", [720, 1080]);
        var requestedFrameRates = ResolveIntListArgument(args, "--fps=", [60, 90, 120]);
        var requestedModes = ResolveStringListArgument(
            args,
            "--modes=",
            ["software", "hardware", "packet"]);
        var decoderRuntimeDirectory = ResolveStringArgument(args, "--decoder-runtime=");
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new ArgumentException("Android stream benchmark requires --device=<adb serial>.");
        }

        var dataDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, "runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var log = new AppLog(dataDirectory);
        using var mirrorRuntime = new AndroidMirrorRuntimeService();
        if (!mirrorRuntime.IsInstalled)
        {
            throw new InvalidOperationException(
                $"scrcpy runtime is missing from '{mirrorRuntime.RuntimeDirectory}'.");
        }

        var adbPath = mirrorRuntime.ResolveAvailableAdbPath()
            ?? throw new InvalidOperationException("ADB is unavailable.");
        var adb = new AdbClient(adbPath);
        var devices = await adb.GetDevicesAsync();
        var device = devices.FirstOrDefault(item => string.Equals(
            item.Serial,
            deviceSerial,
            StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"ADB device '{deviceSerial}' was not found.");
        if (!device.IsOnline)
        {
            throw new InvalidOperationException($"ADB device '{deviceSerial}' is {device.State}.");
        }

        _ = await adb.RunForDeviceAsync(
            device.Serial,
            ["shell", "am", "start", "-a", "android.settings.SETTINGS"],
            CancellationToken.None);
        await Task.Delay(750);

        var stages = new List<AndroidStreamStageReport>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        foreach (var maximumSize in requestedSizes)
        {
            foreach (var requestedFps in requestedFrameRates)
            {
                var dimensions = (Width: 0, Height: 0);
                foreach (var requestedMode in requestedModes)
                {
                    var mode = requestedMode.ToLowerInvariant() switch
                    {
                        "software" => AndroidVideoDecodeMode.Software,
                        "hardware" => AndroidVideoDecodeMode.D3D11Hardware,
                        "packet" => AndroidVideoDecodeMode.PacketOnly,
                        _ => throw new ArgumentException($"Unsupported Android stream mode '{requestedMode}'.")
                    };
                    var stage = await MeasureAndroidStreamStageAsync(
                        mirrorRuntime,
                        log,
                        device,
                        maximumSize,
                        requestedFps,
                        phaseSeconds,
                        mode,
                        decoderRuntimeDirectory,
                        dimensions.Width,
                        dimensions.Height,
                        timeout.Token);
                    stages.Add(stage);
                    if (stage.ActualWidth > 0 && stage.ActualHeight > 0)
                    {
                        dimensions = (stage.ActualWidth, stage.ActualHeight);
                    }
                }
            }
        }

        var report = new AndroidStreamBenchmarkReport(
            device.Serial,
            phaseSeconds,
            stages,
            stages.All(stage => stage.EncodedFramesPerSecond >= 10));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Passed ? 0 : 1;
    }

    private static async Task<AndroidStreamStageReport> MeasureAndroidStreamStageAsync(
        AndroidMirrorRuntimeService mirrorRuntime,
        AppLog log,
        AndroidDeviceInfo device,
        int maximumSize,
        int requestedFps,
        int phaseSeconds,
        AndroidVideoDecodeMode decodeMode,
        string decoderRuntimeDirectory,
        int expectedWidth,
        int expectedHeight,
        CancellationToken cancellationToken)
    {
        await using var session = new ScrcpyAndroidSession(
            mirrorRuntime,
            log,
            decodeMode,
            string.IsNullOrWhiteSpace(decoderRuntimeDirectory)
                ? null
                : decoderRuntimeDirectory);
        var firstFrame = new TaskCompletionSource<AndroidVideoFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (decodeMode != AndroidVideoDecodeMode.PacketOnly)
        {
            session.FrameReceived += (_, frame) => firstFrame.TrySetResult(frame);
        }

        var configuration = new AndroidMirrorConfiguration
        {
            DeviceSerial = device.Serial,
            MaximumSize = maximumSize,
            MaximumFramesPerSecond = requestedFps,
            VideoBitRateMbps = maximumSize == 720 ? 12 : 20
        };
        await session.StartAsync(device, configuration, cancellationToken);

        AndroidVideoFrame? first = null;
        if (decodeMode != AndroidVideoDecodeMode.PacketOnly)
        {
            first = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            expectedWidth = first.Width;
            expectedHeight = first.Height;
        }
        else
        {
            await WaitForCounterAsync(
                () => session.EncodedFramesReceived,
                TimeSpan.FromSeconds(15),
                cancellationToken);
        }

        await Task.Delay(750, cancellationToken);
        var encodedBefore = session.EncodedFramesReceived;
        var decodedBefore = session.DecodedFramesReceived;
        var convertedBefore = session.FramesReceived;
        var conversionBefore = session.PixelConversionTime;
        var hardwareTransferBefore = session.HardwareTransferTime;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var processorTimeBefore = process.TotalProcessorTime;
        var startedAt = DateTimeOffset.Now;
        var touchRequests = await ExerciseAndroidStreamAsync(
            session,
            expectedWidth,
            expectedHeight,
            TimeSpan.FromSeconds(phaseSeconds),
            cancellationToken);
        var endedAt = DateTimeOffset.Now;
        process.Refresh();

        var elapsedSeconds = Math.Max(0.001, (endedAt - startedAt).TotalSeconds);
        var encoded = session.EncodedFramesReceived - encodedBefore;
        var decoded = session.DecodedFramesReceived - decodedBefore;
        var converted = session.FramesReceived - convertedBefore;
        var conversionTime = session.PixelConversionTime - conversionBefore;
        var hardwareTransferTime = session.HardwareTransferTime - hardwareTransferBefore;
        var cpuCoreEquivalent =
            (process.TotalProcessorTime - processorTimeBefore).TotalSeconds /
            elapsedSeconds * 100d;
        var report = new AndroidStreamStageReport(
            decodeMode switch
            {
                AndroidVideoDecodeMode.Software => "software-bgra",
                AndroidVideoDecodeMode.D3D11Hardware => "d3d11va-bgra",
                _ => "packet-only"
            },
            maximumSize,
            requestedFps,
            startedAt,
            endedAt,
            expectedWidth,
            expectedHeight,
            encoded,
            encoded / elapsedSeconds,
            decoded,
            decoded / elapsedSeconds,
            converted,
            converted / elapsedSeconds,
            converted == 0 ? 0 : conversionTime.TotalMilliseconds / converted,
            converted == 0 ? 0 : hardwareTransferTime.TotalMilliseconds / converted,
            touchRequests,
            cpuCoreEquivalent,
            cpuCoreEquivalent / Environment.ProcessorCount,
            process.WorkingSet64 / 1024d / 1024d);
        Console.WriteLine(
            $"Android stream {maximumSize}p/{requestedFps} {report.Mode}: " +
            $"packets={report.EncodedFramesPerSecond:F2}, " +
            $"decoded={report.DecodedFramesPerSecond:F2}, " +
            $"converted={report.ConvertedFramesPerSecond:F2} FPS, " +
            $"CPU={report.ProcessCpuCoreEquivalentPercent:F1}% core");
        return report;
    }

    private static async Task<int> ExerciseAndroidStreamAsync(
        ScrcpyAndroidSession session,
        int screenWidth,
        int screenHeight,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var touching = false;
        var requests = 0;
        _ = timeBeginPeriod(1);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 120d));
            while (stopwatch.Elapsed < duration &&
                   await timer.WaitForNextTickAsync(cancellationToken))
            {
                const double halfCycleSeconds = 1.2d;
                var cycle = (int)(stopwatch.Elapsed.TotalSeconds / halfCycleSeconds);
                var cycleElapsed = stopwatch.Elapsed.TotalSeconds % halfCycleSeconds;
                var reverse = cycle % 2 == 1;
                var progress = Math.Clamp(cycleElapsed / halfCycleSeconds, 0d, 1d);
                var eased = progress * progress * (3d - 2d * progress);
                var y = reverse
                    ? 0.20d + 0.60d * eased
                    : 0.80d - 0.60d * eased;
                var touch = new AndroidTouchEvent(
                    !touching ? AndroidTouchAction.Down : AndroidTouchAction.Move,
                    (int)Math.Round(screenWidth * 0.5d),
                    (int)Math.Round(screenHeight * y),
                    screenWidth,
                    screenHeight);

                if (session.QueueTouch(touch))
                {
                    requests++;
                }
                touching = true;
            }
        }
        finally
        {
            _ = timeEndPeriod(1);
        }

        if (touching && session.QueueTouch(new AndroidTouchEvent(
                AndroidTouchAction.Up,
                screenWidth / 2,
                screenHeight / 2,
                screenWidth,
                screenHeight)))
        {
            requests++;
        }
        await Task.Delay(150, cancellationToken);
        return requests;
    }

    private static async Task WaitForCounterAsync(
        Func<long> counter,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (counter() == 0 && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(25, cancellationToken);
        }
        if (counter() == 0)
        {
            throw new TimeoutException("Android stream did not produce an H.264 media packet.");
        }
    }

    private static async Task<int> RunAndroidMirrorAsync(string[] args, string outputPath)
    {
        var deviceSerial = ResolveStringArgument(args, "--device=");
        var phaseSeconds = ResolveIntArgument(args, "--phase-seconds=", 8, 4, 30);
        var requestedSourceFps = ResolveIntArgument(args, "--fps=", 120, 10, 120);
        var requestedOverlayFps = ResolveIntArgument(args, "--overlay-fps=", 120, 1, 120);
        var requestedSizes = ResolveIntListArgument(args, "--sizes=", [720, 1080]);
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            throw new ArgumentException("Android mirror benchmark requires --device=<adb serial>.");
        }

        var dataDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, "runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var configuration = new AppConfiguration();
        var log = new AppLog(dataDirectory);
        using var mirrorRuntime = new AndroidMirrorRuntimeService();
        if (!mirrorRuntime.IsInstalled)
        {
            throw new InvalidOperationException(
                $"scrcpy runtime is missing from '{mirrorRuntime.RuntimeDirectory}'.");
        }
        var adbPath = mirrorRuntime.ResolveAvailableAdbPath()
            ?? throw new InvalidOperationException("ADB is unavailable.");
        var adb = new AdbClient(adbPath);
        var devices = await adb.GetDevicesAsync();
        var device = devices.FirstOrDefault(item => string.Equals(
            item.Serial,
            deviceSerial,
            StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"ADB device '{deviceSerial}' was not found.");
        if (!device.IsOnline)
        {
            throw new InvalidOperationException($"ADB device '{deviceSerial}' is {device.State}.");
        }

        _ = await adb.RunForDeviceAsync(
            device.Serial,
            ["shell", "am", "start", "-a", "android.settings.SETTINGS"],
            CancellationToken.None);
        await using var runtime = new SteamVrTranslationRuntime(configuration, log);
        var renderedFrames = new ConcurrentQueue<WpfOverlayFrameRenderedEventArgs>();
        var runtimeFrames = new ConcurrentQueue<DiagnosticRuntimeFrameCompletedEventArgs>();
        var pointerEvents = new ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs>();
        runtime.WpfOverlayFrameRendered += (_, frame) => renderedFrames.Enqueue(frame);
        runtime.DiagnosticRuntimeFrameCompleted += (_, frame) => runtimeFrames.Enqueue(frame);
        runtime.DiagnosticWpfPointerProcessed += (_, pointer) => pointerEvents.Enqueue(pointer);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await runtime.StartAsyncForDiagnostics();

        var controlPanel = new VrControlPanelWindow(new VrControlPanelState(
            VoiceEnabled: true,
            TranslationEnabled: false,
            SendImmediately: true,
            DisplayMode: VoiceTranslationDisplayModes.TranslationOnly,
            TargetLanguage: "en-US",
            ChunkIntervalMilliseconds: 1000,
            CaptureEye: "left-eye"))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        controlPanel.Show();
        controlPanel.UpdateLayout();
        var controlPanelId = await runtime.ShowWindowAsync(
            controlPanel,
            new WpfSpatialOverlayOptions
            {
                Name = "Android Benchmark Control Panel",
                WidthMeters = 0.25f,
                Placement = WpfSpatialOverlayPlacement.LeftHand,
                CanGrab = true,
                ShowToolbarWhenGrabbed = false,
                MaximumFramesPerSecond = requestedOverlayFps
            },
            timeout.Token);

        var stages = new List<AndroidMirrorStageReport>();
        try
        {
            foreach (var maximumSize in requestedSizes.Where(value => value is 720 or 1080))
            {
                await using var session = new ScrcpyAndroidSession(
                    mirrorRuntime,
                    log,
                    AndroidVideoDecodeMode.Software,
                    mirrorRuntime.DecoderRuntimeDirectory);
                var firstFrame = new TaskCompletionSource<AndroidVideoFrame>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                session.FrameReceived += (_, frame) => firstFrame.TrySetResult(frame);
                var mirrorConfiguration = new AndroidMirrorConfiguration
                {
                    DeviceSerial = device.Serial,
                    MaximumSize = maximumSize,
                    MaximumFramesPerSecond = requestedSourceFps,
                    VideoBitRateMbps = maximumSize == 720 ? 8 : 12,
                    WindowWidthMeters = AndroidMirrorConfiguration.DefaultWindowWidthMeters
                };
                var window = new AndroidMirrorWindow(session)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false
                };
                long? overlayId = null;
                try
                {
                    await session.StartAsync(device, mirrorConfiguration, timeout.Token);
                    var first = await firstFrame.Task.WaitAsync(
                        TimeSpan.FromSeconds(15),
                        timeout.Token);
                    window.ConfigureForSource(
                        first.Width,
                        first.Height,
                        mirrorConfiguration.WindowWidthMeters);
                    overlayId = await runtime.ShowWindowAsync(
                        window,
                        new WpfSpatialOverlayOptions
                        {
                            Name = $"Android Mirror {maximumSize}p",
                            WidthMeters = (float)window.RecommendedWidthMeters,
                            DistanceMeters = 0.72f,
                            Placement = WpfSpatialOverlayPlacement.Head,
                            CanGrab = true,
                            ShowToolbarWhenGrabbed = true,
                            MaximumFramesPerSecond = requestedOverlayFps
                        },
                        timeout.Token);
                    var openVrInteraction = await VerifyAndroidMirrorScreenInteractionAsync(
                        runtime,
                        window,
                        session,
                        overlayId.Value,
                        pointerEvents,
                        timeout.Token);
                    await Task.Delay(800, timeout.Token);
                    Drain(renderedFrames);
                    Drain(runtimeFrames);
                    Drain(pointerEvents);

                    var sourceFrameStart = session.FramesReceived;
                    var startedAt = DateTimeOffset.Now;
                    var process = Process.GetCurrentProcess();
                    var processorTimeBefore = process.TotalProcessorTime;
                    var exercise = await ExerciseAndroidMirrorAsync(
                        runtime,
                        session,
                        overlayId.Value,
                        controlPanelId,
                        first.Width,
                        first.Height,
                        requestedOverlayFps,
                        TimeSpan.FromSeconds(phaseSeconds),
                        timeout.Token);
                    var endedAt = DateTimeOffset.Now;
                    process.Refresh();
                    var elapsedSeconds = Math.Max(0.001, (endedAt - startedAt).TotalSeconds);
                    var wpfFrames = Drain(renderedFrames);
                    var loopFrames = Drain(runtimeFrames);
                    var pointers = Drain(pointerEvents);
                    var phoneFrames = session.FramesReceived - sourceFrameStart;
                    var mirrorFrames = wpfFrames
                        .Where(frame =>
                            frame.OverlayId == overlayId.Value &&
                            frame.IsDirectPixels)
                        .ToArray();
                    var intervals = RuntimeFrameIntervals(loopFrames);
                    var cpuCoreEquivalent =
                        (process.TotalProcessorTime - processorTimeBefore).TotalSeconds /
                        elapsedSeconds * 100d;
                    stages.Add(new AndroidMirrorStageReport(
                        maximumSize,
                        startedAt,
                        endedAt,
                        first.Width,
                        first.Height,
                        window.RecommendedWidthMeters,
                        window.RecommendedHeightMeters,
                        0.72,
                        AngularSizeDegrees(window.RecommendedWidthMeters, 0.72),
                        AngularSizeDegrees(window.RecommendedHeightMeters, 0.72),
                        phoneFrames,
                        phoneFrames / elapsedSeconds,
                        mirrorFrames.Length,
                        mirrorFrames.Length / elapsedSeconds,
                        EffectiveRuntimeFramesPerSecond(loopFrames),
                        PercentileOrZero(
                            loopFrames.Select(frame => frame.WorkDuration.TotalMilliseconds),
                            0.95),
                        PercentileOrZero(intervals, 0.95),
                        exercise.PointerRequests,
                        exercise.SourceTouchRequests,
                        openVrInteraction.ScreenTouchEventsQueued,
                        openVrInteraction.ScreenTouchPassed,
                        openVrInteraction.EdgeConstraintPassed,
                        pointers.Count,
                        EffectivePointerUpdatesPerSecond(pointers),
                        PercentileOrZero(
                            pointers.Select(pointer => pointer.QueueDuration.TotalMilliseconds),
                            0.95),
                        PercentileOrZero(
                            mirrorFrames.Select(frame => frame.RasterizeDuration.TotalMilliseconds),
                            0.95),
                        PercentileOrZero(
                            mirrorFrames.Select(frame => frame.UploadDuration.TotalMilliseconds),
                            0.95),
                        PercentileOrZero(
                            mirrorFrames.Select(frame => frame.TotalDuration.TotalMilliseconds),
                            0.95),
                        cpuCoreEquivalent,
                        cpuCoreEquivalent / Environment.ProcessorCount,
                        process.WorkingSet64 / 1024d / 1024d));
                }
                finally
                {
                    if (overlayId is { } id)
                    {
                        await runtime.CloseWindowAsync(id, CancellationToken.None);
                    }
                    await session.StopAsync();
                    if (window.IsLoaded)
                    {
                        window.Close();
                    }
                }
            }
        }
        finally
        {
            await runtime.CloseWindowAsync(controlPanelId, CancellationToken.None);
            controlPanel.Close();
        }

        var report = new AndroidMirrorBenchmarkReport(
            requestedOverlayFps,
            requestedSourceFps,
            device.Serial,
            phaseSeconds,
            stages,
            stages.All(stage =>
                Math.Max(stage.ActualWidth, stage.ActualHeight) == stage.MaximumSize &&
                stage.OpenVrScreenTouchPassed &&
                stage.OpenVrEdgeConstraintPassed &&
                stage.RuntimeLoopFramesPerSecond >= requestedOverlayFps * 0.95 &&
                stage.TextureUploadsPerSecond >= stage.PhoneFramesPerSecond * 0.90 &&
                stage.PointerUpdatesPerSecond >= requestedOverlayFps * 0.90 &&
                stage.PhoneFramesPerSecond >= requestedSourceFps * 0.70));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Passed ? 0 : 1;
    }

    private static async Task<(int PointerRequests, int SourceTouchRequests)> ExerciseAndroidMirrorAsync(
        SteamVrTranslationRuntime runtime,
        ScrcpyAndroidSession session,
        long mirrorOverlayId,
        long controlPanelId,
        int screenWidth,
        int screenHeight,
        int requestedFramesPerSecond,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var frameIndex = 0;
        var requests = 0;
        var sourceExercise = ExerciseAndroidStreamAsync(
            session,
            screenWidth,
            screenHeight,
            duration,
            cancellationToken);
        _ = timeBeginPeriod(1);
        try
        {
            using var timer = new PeriodicTimer(
                TimeSpan.FromSeconds(1d / requestedFramesPerSecond));
            while (stopwatch.Elapsed < duration &&
                   await timer.WaitForNextTickAsync(cancellationToken))
            {
                const double halfCycleSeconds = 1.2d;
                var cycle = (int)(stopwatch.Elapsed.TotalSeconds / halfCycleSeconds);
                var cycleElapsed = stopwatch.Elapsed.TotalSeconds % halfCycleSeconds;
                var reverse = cycle % 2 == 1;
                var progress = (float)Math.Clamp(cycleElapsed / halfCycleSeconds, 0d, 1d);
                var y = reverse
                    ? 0.20f + 0.60f * progress
                    : 0.80f - 0.60f * progress;
                if (runtime.MoveWindowPointerForDiagnostics(
                        mirrorOverlayId,
                        new NormalizedPoint(0.5f, y),
                        false,
                        dispatchInput: true) != 0)
                {
                    requests++;
                }
                if (frameIndex % Math.Max(1, requestedFramesPerSecond / 2) == 0)
                {
                    runtime.InvalidateWindow(controlPanelId);
                }
                frameIndex++;
            }
        }
        finally
        {
            _ = timeEndPeriod(1);
        }
        runtime.MoveWindowPointerForDiagnostics(
            mirrorOverlayId,
            new NormalizedPoint(0.5f, 0.5f),
            false,
            dispatchInput: true);
        await Task.Delay(250, cancellationToken);
        return (requests + 1, await sourceExercise);
    }

    private static async Task<AndroidOpenVrInteractionCheck> VerifyAndroidMirrorScreenInteractionAsync(
        SteamVrTranslationRuntime runtime,
        AndroidMirrorWindow window,
        ScrcpyAndroidSession session,
        long overlayId,
        ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs> events,
        CancellationToken cancellationToken)
    {
        Drain(events);
        var region = window.DirectPixelRegion;
        var screenCenter = new NormalizedPoint(
            region.X + (region.Width / 2f),
            region.Y + (region.Height / 2f));
        var touchCountBefore = session.TouchEventsQueued;

        var downRequest = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
            overlayId,
            screenCenter,
            isPressed: true,
            dispatchInput: true);
        var down = await WaitForPointerAsync(events, downRequest, cancellationToken);
        var upRequest = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
            overlayId,
            screenCenter,
            isPressed: false,
            dispatchInput: true);
        var up = await WaitForPointerAsync(events, upRequest, cancellationToken);
        var queuedTouchEvents = checked((int)(session.TouchEventsQueued - touchCountBefore));

        var nearEdgeRequest = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
            overlayId,
            new NormalizedPoint(0.999f, 0.5f));
        var nearEdge = await WaitForPointerAsync(events, nearEdgeRequest, cancellationToken);
        var outsideRequest = runtime.MoveWindowPointerThroughOpenVrForDiagnostics(
            overlayId,
            new NormalizedPoint(1.001f, 0.5f));
        var outside = await WaitForPointerAsync(events, outsideRequest, cancellationToken);
        _ = runtime.LeaveWindowPointerForDiagnostics(overlayId);

        return new AndroidOpenVrInteractionCheck(
            queuedTouchEvents,
            queuedTouchEvents >= 2 &&
            down.ResolvedTexturePoint is not null &&
            up.ResolvedTexturePoint is not null,
            nearEdge.ResolvedTexturePoint is { X: >= 0.999f } &&
            outside.ResolvedTexturePoint is null);
    }

    private static async Task<int> RunStressAsync(string[] args, string outputPath)
    {
        var maximumWindows = ResolveIntArgument(args, "--max-windows=", 12, 1, 32);
        var phaseSeconds = ResolveIntArgument(args, "--phase-seconds=", 5, 2, 30);
        var phaseDuration = TimeSpan.FromSeconds(phaseSeconds);
        var dataDirectory = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            "runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var configuration = new AppConfiguration();
        var log = new AppLog(dataDirectory);
        await using var runtime = new SteamVrTranslationRuntime(configuration, log);
        var frames = new ConcurrentQueue<WpfOverlayFrameRenderedEventArgs>();
        runtime.WpfOverlayFrameRendered += (_, frame) => frames.Enqueue(frame);
        var windows = new List<Window>();
        var overlayIds = new List<long>();
        var stages = new List<OverlayStressStageReport>();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(45 + (maximumWindows * (phaseSeconds + 2))));

        try
        {
            await runtime.StartAsyncForDiagnostics();
            for (var windowIndex = 0; windowIndex < maximumWindows; windowIndex++)
            {
                var (window, options) = CreateStressWindow(windowIndex, configuration);
                window.Show();
                window.UpdateLayout();
                windows.Add(window);
                overlayIds.Add(await runtime.ShowWindowAsync(window, options, timeout.Token));

                await Task.Delay(500, timeout.Token);
                Drain(frames);
                var startedAt = DateTimeOffset.Now;
                var process = Process.GetCurrentProcess();
                var processorTimeBefore = process.TotalProcessorTime;
                var measurement = await MeasureManyAsync(
                    runtime,
                    frames,
                    overlayIds,
                    phaseDuration,
                    timeout.Token);
                var measured = measurement.Frames;
                var endedAt = DateTimeOffset.Now;
                process.Refresh();
                var processorTime = process.TotalProcessorTime - processorTimeBefore;
                var elapsedSeconds = Math.Max(0.001, (endedAt - startedAt).TotalSeconds);
                var cpuCoreEquivalentPercent =
                    processorTime.TotalSeconds / elapsedSeconds * 100d;
                var perWindow = overlayIds
                    .Select(id => Summarize(measured, id))
                    .ToArray();
                var stage = new OverlayStressStageReport(
                    WindowCount: overlayIds.Count,
                    StartedAt: startedAt,
                    EndedAt: endedAt,
                    TotalFrames: measured.Count,
                    MinimumFramesPerSecond: perWindow.Min(item => item.EffectiveFramesPerSecond),
                    AverageFramesPerSecond: perWindow.Average(item => item.EffectiveFramesPerSecond),
                    RandomPointerMoveRequests: measurement.RandomPointerMoveRequests,
                    ProcessCpuCoreEquivalentPercent: cpuCoreEquivalentPercent,
                    ProcessCpuTotalCapacityPercent:
                        cpuCoreEquivalentPercent / Environment.ProcessorCount,
                    ProcessWorkingSetMiB: process.WorkingSet64 / 1024d / 1024d,
                    CombinedP95FrameMilliseconds: Percentile(
                        measured
                            .GroupBy(frame => frame.RenderedAt)
                            .Select(group => group.Sum(frame => frame.TotalDuration.TotalMilliseconds)),
                        0.95),
                    Windows: perWindow);
                stages.Add(stage);
                Console.WriteLine(
                    $"Stress {stage.WindowCount}: min={stage.MinimumFramesPerSecond:F2} FPS, " +
                    $"avg={stage.AverageFramesPerSecond:F2} FPS, " +
                    $"combined-p95={stage.CombinedP95FrameMilliseconds:F2} ms");
                if (stage.MinimumFramesPerSecond < 59.5)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var overlayId in overlayIds)
            {
                try
                {
                    await runtime.CloseWindowAsync(overlayId, CancellationToken.None);
                }
                catch
                {
                }
            }
            foreach (var window in windows)
            {
                if (window is SubtitleHistoryWindow subtitleWindow)
                {
                    subtitleWindow.ClosePermanently();
                }
                else
                {
                    window.Close();
                }
            }
        }

        var firstBelowTarget = stages
            .FirstOrDefault(stage => stage.MinimumFramesPerSecond < 59.5)
            ?.WindowCount;
        var report = new OverlayStressReport(
            TargetFramesPerSecond: 60,
            PassingThresholdFramesPerSecond: 59.5,
            TextureLongEdge: OverlayRenderer.DefaultTextureLongEdge,
            PhaseSeconds: phaseSeconds,
            RequestedMaximumWindows: maximumWindows,
            MaximumSustainedWindowCount: stages
                .Where(stage => stage.MinimumFramesPerSecond >= 59.5)
                .Select(stage => stage.WindowCount)
                .DefaultIfEmpty(0)
                .Max(),
            FirstWindowCountBelowTarget: firstBelowTarget,
            Stages: stages);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> RunRealisticStressAsync(string[] args, string outputPath)
    {
        var maximumWindows = ResolveIntArgument(args, "--max-windows=", 32, 1, 32);
        var phaseSeconds = ResolveIntArgument(args, "--phase-seconds=", 8, 2, 30);
        var phaseDuration = TimeSpan.FromSeconds(phaseSeconds);
        var dataDirectory = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            "runtime-data");
        Directory.CreateDirectory(dataDirectory);
        var configuration = new AppConfiguration();
        var log = new AppLog(dataDirectory);
        await using var runtime = new SteamVrTranslationRuntime(configuration, log);
        var renderedFrames = new ConcurrentQueue<WpfOverlayFrameRenderedEventArgs>();
        var resultFrames = new ConcurrentQueue<ResultOverlayFrameRenderedEventArgs>();
        var runtimeFrames = new ConcurrentQueue<DiagnosticRuntimeFrameCompletedEventArgs>();
        var pointerEvents = new ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs>();
        var interactionEvents = new ConcurrentQueue<DiagnosticWpfInteractionCompletedEventArgs>();
        runtime.WpfOverlayFrameRendered += (_, frame) => renderedFrames.Enqueue(frame);
        runtime.ResultOverlayFrameRendered += (_, frame) => resultFrames.Enqueue(frame);
        runtime.DiagnosticRuntimeFrameCompleted += (_, frame) => runtimeFrames.Enqueue(frame);
        runtime.DiagnosticWpfPointerProcessed += (_, pointer) => pointerEvents.Enqueue(pointer);
        runtime.DiagnosticWpfInteractionCompleted += (_, interaction) => interactionEvents.Enqueue(interaction);

        var contexts = new List<RealisticWindowContext>();
        var stages = new List<OverlayRealisticStageReport>();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(45 + (maximumWindows * (phaseSeconds + 2))));

        try
        {
            await runtime.StartAsyncForDiagnostics();
            for (var windowIndex = 0; windowIndex < maximumWindows; windowIndex++)
            {
                RealisticWindowContext context;
                if (windowIndex < 2)
                {
                    context = CreateRealisticWpfWindow(windowIndex, configuration);
                    context.Window!.Show();
                    context.Window.UpdateLayout();
                    context.OverlayId = await runtime.ShowWindowAsync(
                        context.Window,
                        context.Options!,
                        timeout.Token);
                }
                else if (windowIndex % 2 == 0)
                {
                    context = RealisticWindowContext.CreateMarkdown();
                    context.OverlayId = await runtime.ShowMarkdownResultForDiagnostics(
                        CreateRealisticMarkdown(windowIndex),
                        timeout.Token);
                }
                else
                {
                    context = RealisticWindowContext.CreateChat(CreateRealisticChatHistory());
                    context.OverlayId = await runtime.ShowChatResultForDiagnostics(
                        CreateRealisticChatView(context.ChatHistory!, context.ChatRevision),
                        timeout.Token);
                }
                contexts.Add(context);

                await Task.Delay(500, timeout.Token);
                Drain(renderedFrames);
                Drain(resultFrames);
                Drain(runtimeFrames);
                Drain(pointerEvents);
                Drain(interactionEvents);

                var startedAt = DateTimeOffset.Now;
                var process = Process.GetCurrentProcess();
                var processorTimeBefore = process.TotalProcessorTime;
                var measurement = await MeasureRealisticPhaseAsync(
                    runtime,
                    renderedFrames,
                    resultFrames,
                    runtimeFrames,
                    pointerEvents,
                    interactionEvents,
                    contexts,
                    phaseDuration,
                    timeout.Token);
                var endedAt = DateTimeOffset.Now;
                process.Refresh();
                var processorTime = process.TotalProcessorTime - processorTimeBefore;
                var elapsedSeconds = Math.Max(0.001, (endedAt - startedAt).TotalSeconds);
                var cpuCoreEquivalentPercent = processorTime.TotalSeconds / elapsedSeconds * 100d;
                var successfulInteractions = measurement.Interactions
                    .Where(item => item.Succeeded)
                    .ToArray();
                var controlPanelFrames = measurement.RenderedFrames
                    .Where(item => item.Name.StartsWith(
                        "Realistic Control Panel",
                        StringComparison.Ordinal))
                    .ToArray();
                var subtitleFrames = measurement.RenderedFrames
                    .Where(item => item.Name.StartsWith(
                        "Realistic Subtitle Window",
                        StringComparison.Ordinal))
                    .ToArray();
                var markdownFrames = measurement.ResultFrames
                    .Where(item => !item.IsChat)
                    .ToArray();
                var chatFrames = measurement.ResultFrames
                    .Where(item => item.IsChat)
                    .ToArray();
                var runtimeFrameIntervals = RuntimeFrameIntervals(measurement.RuntimeFrames);
                var runtimeDeadlineMisses = runtimeFrameIntervals.Count(value => value > 1000d / 60d);
                var stage = new OverlayRealisticStageReport(
                    WindowCount: contexts.Count,
                    ControlPanelCount: contexts.Count(item => item.Kind == RealisticWindowKind.ControlPanel),
                    SubtitleWindowCount: contexts.Count(item => item.Kind == RealisticWindowKind.Subtitle),
                    MarkdownWindowCount: contexts.Count(item => item.Kind == RealisticWindowKind.Markdown),
                    ChatWindowCount: contexts.Count(item => item.Kind == RealisticWindowKind.Chat),
                    StartedAt: startedAt,
                    EndedAt: endedAt,
                    RuntimeLoopFrames: measurement.RuntimeFrames.Count,
                    RuntimeLoopFramesPerSecond: EffectiveRuntimeFramesPerSecond(measurement.RuntimeFrames),
                    P95RuntimeWorkMilliseconds: PercentileOrZero(
                        measurement.RuntimeFrames.Select(item => item.WorkDuration.TotalMilliseconds),
                        0.95),
                    P95RuntimeFrameIntervalMilliseconds: PercentileOrZero(runtimeFrameIntervals, 0.95),
                    P99RuntimeFrameIntervalMilliseconds: PercentileOrZero(runtimeFrameIntervals, 0.99),
                    RuntimeDeadlineMisses: runtimeDeadlineMisses,
                    RuntimeDeadlineMissPercent: runtimeDeadlineMisses /
                        (double)Math.Max(1, runtimeFrameIntervals.Count) * 100d,
                    WpfContentUploads: measurement.RenderedFrames.Count,
                    P95WpfRasterizeMilliseconds: PercentileOrZero(
                        measurement.RenderedFrames.Select(item => item.RasterizeDuration.TotalMilliseconds),
                        0.95),
                    P95WpfUploadMilliseconds: PercentileOrZero(
                        measurement.RenderedFrames.Select(item => item.UploadDuration.TotalMilliseconds),
                        0.95),
                    P95WpfTotalMilliseconds: PercentileOrZero(
                        measurement.RenderedFrames.Select(item => item.TotalDuration.TotalMilliseconds),
                        0.95),
                    ControlPanelContentUploads: controlPanelFrames.Length,
                    SubtitleContentUploads: subtitleFrames.Length,
                    SubtitleContentUpdatesPerSecond: subtitleFrames.Length / elapsedSeconds,
                    P95SubtitleRasterizeMilliseconds: PercentileOrZero(
                        subtitleFrames.Select(item => item.RasterizeDuration.TotalMilliseconds),
                        0.95),
                    MarkdownContentUploads: markdownFrames.Length,
                    MarkdownContentUpdatesPerSecond: markdownFrames.Length / elapsedSeconds,
                    P95MarkdownRasterizeMilliseconds: PercentileOrZero(
                        markdownFrames.Select(item => item.RasterizeDuration.TotalMilliseconds),
                        0.95),
                    P95MarkdownUploadMilliseconds: PercentileOrZero(
                        markdownFrames.Select(item => item.UploadDuration.TotalMilliseconds),
                        0.95),
                    P95MarkdownTotalMilliseconds: PercentileOrZero(
                        markdownFrames.Select(item => item.TotalDuration.TotalMilliseconds),
                        0.95),
                    ChatContentUploads: chatFrames.Length,
                    ChatContentUpdatesPerSecond: chatFrames.Length / elapsedSeconds,
                    P95ChatRasterizeMilliseconds: PercentileOrZero(
                        chatFrames.Select(item => item.RasterizeDuration.TotalMilliseconds),
                        0.95),
                    P95ChatUploadMilliseconds: PercentileOrZero(
                        chatFrames.Select(item => item.UploadDuration.TotalMilliseconds),
                        0.95),
                    P95ChatTotalMilliseconds: PercentileOrZero(
                        chatFrames.Select(item => item.TotalDuration.TotalMilliseconds),
                        0.95),
                    PointerMoveRequests: measurement.PointerMoveRequests,
                    PointerMovesProcessed: measurement.PointerEvents.Count,
                    PointerUpdatesPerSecond: EffectivePointerUpdatesPerSecond(measurement.PointerEvents),
                    P95PointerQueueMilliseconds: PercentileOrZero(
                        measurement.PointerEvents.Select(item => item.QueueDuration.TotalMilliseconds),
                        0.95),
                    UiClickRequests: measurement.UiClickRequests,
                    SubtitleScrollRequests: measurement.SubtitleScrollRequests,
                    MarkdownScrollRequests: measurement.MarkdownScrollRequests,
                    ChatUpdateRequests: measurement.ChatUpdateRequests,
                    InteractionCompletions: successfulInteractions.Length,
                    InteractionFailures: measurement.Interactions.Count(item => !item.Succeeded),
                    P50InteractionToTextureMilliseconds: PercentileOrZero(
                        successfulInteractions.Select(item => item.RequestToTextureDuration.TotalMilliseconds),
                        0.50),
                    P95InteractionToTextureMilliseconds: PercentileOrZero(
                        successfulInteractions.Select(item => item.RequestToTextureDuration.TotalMilliseconds),
                        0.95),
                    MaximumInteractionToTextureMilliseconds: successfulInteractions
                        .Select(item => item.RequestToTextureDuration.TotalMilliseconds)
                        .DefaultIfEmpty(0)
                        .Max(),
                    ProcessCpuCoreEquivalentPercent: cpuCoreEquivalentPercent,
                    ProcessCpuTotalCapacityPercent: cpuCoreEquivalentPercent / Environment.ProcessorCount,
                    ProcessWorkingSetMiB: process.WorkingSet64 / 1024d / 1024d);
                stages.Add(stage);
                Console.WriteLine(
                    $"Realistic {stage.WindowCount}: loop={stage.RuntimeLoopFramesPerSecond:F2} FPS, " +
                    $"work-p95={stage.P95RuntimeWorkMilliseconds:F2} ms, " +
                    $"input-p95={stage.P95InteractionToTextureMilliseconds:F2} ms, " +
                    $"wpf={stage.WpfContentUploads}, markdown={stage.MarkdownContentUploads}, " +
                    $"chat={stage.ChatContentUploads}");
                if (stage.RuntimeLoopFramesPerSecond < 59.5 &&
                    contexts.Any(item => item.Kind == RealisticWindowKind.Markdown) &&
                    contexts.Any(item => item.Kind == RealisticWindowKind.Chat))
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var context in contexts)
            {
                try
                {
                    if (context.Kind is RealisticWindowKind.Markdown or RealisticWindowKind.Chat)
                    {
                        await runtime.CloseOverlayForDiagnostics(context.OverlayId, CancellationToken.None);
                    }
                    else
                    {
                        await runtime.CloseWindowAsync(context.OverlayId, CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
            foreach (var context in contexts)
            {
                if (context.Window is SubtitleHistoryWindow subtitleWindow)
                {
                    subtitleWindow.ClosePermanently();
                }
                else if (context.Window is not null)
                {
                    context.Window.Close();
                }
            }
        }

        var firstBelowTarget = stages
            .FirstOrDefault(stage => stage.RuntimeLoopFramesPerSecond < 59.5)
            ?.WindowCount;
        var report = new OverlayRealisticStressReport(
            TargetFramesPerSecond: 60,
            PassingThresholdFramesPerSecond: 59.5,
            TextureLongEdge: OverlayRenderer.DefaultTextureLongEdge,
            PhaseSeconds: phaseSeconds,
            RequestedMaximumWindows: maximumWindows,
            MaximumSustainedWindowCount: stages
                .Where(stage => stage.RuntimeLoopFramesPerSecond >= 59.5)
                .Select(stage => stage.WindowCount)
                .DefaultIfEmpty(0)
                .Max(),
            FirstWindowCountBelowTarget: firstBelowTarget,
            Workload:
            [
                "One controller pointer moves randomly at 60 Hz across the WPF control and subtitle windows.",
                "A control-panel button is clicked every 650 ms through the WPF pointer path.",
                "A subtitle viewport receives a real mouse-wheel event every 350 ms.",
                "The subtitle window receives streaming text updates at 4 Hz and a new entry every 1.5 s.",
                "One production Markdown result window scrolls continuously at 60 Hz; the active target rotates between Markdown windows.",
                "One production chat result receives a growing streaming answer at 10 Hz while completed conversation history remains visible."
            ],
            Stages: stages);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static RealisticWindowContext CreateRealisticWpfWindow(
        int windowIndex,
        AppConfiguration configuration)
    {
        if (windowIndex == 0)
        {
            var window = new VrControlPanelWindow(new VrControlPanelState(
                VoiceEnabled: true,
                TranslationEnabled: true,
                SendImmediately: true,
                DisplayMode: VoiceTranslationDisplayModes.OriginalThenTranslation,
                TargetLanguage: "zh-CN",
                ChunkIntervalMilliseconds: 1000,
                CaptureEye: "left-eye"))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false
            };
            return new RealisticWindowContext(
                window,
                new WpfSpatialOverlayOptions
                {
                    Name = $"Realistic Control Panel {windowIndex + 1}",
                    WidthMeters = 0.25f,
                    Placement = WpfSpatialOverlayPlacement.Head,
                    CanGrab = true,
                    ShowToolbarWhenGrabbed = false,
                    MaximumFramesPerSecond = 60
                },
                RealisticWindowKind.ControlPanel,
                null);
        }

        var history = new SubtitleHistoryViewModel(configuration.Subtitles);
        SubtitleHistoryEntry? activeEntry = null;
        for (var entryIndex = 0; entryIndex < 8; entryIndex++)
        {
            activeEntry = history.Add(
                entryIndex % 3,
                TimeSpan.FromSeconds(entryIndex * 3),
                TimeSpan.FromSeconds((entryIndex + 1) * 3),
                $"Speaker {entryIndex % 3 + 1}: realistic subtitle sample {entryIndex + 1} for scrolling.");
            activeEntry.TranslatedText = $"说话人 {entryIndex % 3 + 1}：用于滚动测试的实时字幕样本 {entryIndex + 1}。";
            activeEntry.IsTranslating = false;
        }
        var subtitle = new SubtitleHistoryWindow(history)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        return new RealisticWindowContext(
            subtitle,
            MainWindow.CreateSubtitleOverlayOptions(
                configuration.Subtitles,
                WpfSpatialOverlayPlacement.Head) with
            {
                Name = $"Realistic Subtitle Window {windowIndex + 1}"
            },
            RealisticWindowKind.Subtitle,
            history)
        {
            ActiveSubtitleEntry = activeEntry
        };
    }

    private static string CreateRealisticMarkdown(int windowIndex)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine($"# Translation result {windowIndex - 1}");
        markdown.AppendLine();
        markdown.AppendLine("> Continuously scrolling production Markdown overlay benchmark.");
        markdown.AppendLine();
        for (var section = 1; section <= 18; section++)
        {
            markdown.AppendLine($"## Section {section:D2}");
            markdown.AppendLine();
            markdown.AppendLine(
                "This paragraph simulates a translated explanation with **emphasis**, " +
                "inline `controls`, and enough content to exercise layout while the viewport scrolls.");
            markdown.AppendLine();
            markdown.AppendLine($"- Source detail {section}: spatial context and visible labels");
            markdown.AppendLine($"- Translation note {section}: concise localized interpretation");
            markdown.AppendLine($"- Action {section}: continue reading the next section");
            markdown.AppendLine();
        }
        return markdown.ToString();
    }

    private static IReadOnlyList<AssistantConversationTurn> CreateRealisticChatHistory() =>
    [
        new(
            "What does the warning label in the captured image mean?",
            "It warns that the cover must remain closed while the device is operating."),
        new(
            "Explain the second line in simpler terms.",
            "Disconnect the power before cleaning or opening the enclosure."),
        new(
            "Is there anything time-sensitive in the notice?",
            "Yes. The inspection must be completed before the date printed near the lower edge."),
        new(
            "Summarize the important actions.",
            "Keep the cover closed, disconnect power before service, and complete the inspection on time.")
    ];

    private static AssistantConversationView CreateRealisticChatView(
        IReadOnlyList<AssistantConversationTurn> history,
        int revision)
    {
        var segmentCount = Math.Clamp((revision % 40) + 1, 1, 40);
        var answer = new StringBuilder();
        for (var index = 0; index < segmentCount; index++)
        {
            if (index > 0)
            {
                answer.Append(' ');
            }
            answer.Append(
                $"Streaming segment {index + 1}: the visible controls indicate a safe operating sequence, " +
                "with concise guidance generated from the captured scene.");
        }
        return new AssistantConversationView(
            history,
            "Continue the explanation and relate it to the visible controls.",
            answer.ToString());
    }

    private static (Window Window, WpfSpatialOverlayOptions Options) CreateStressWindow(
        int windowIndex,
        AppConfiguration configuration)
    {
        if (windowIndex % 2 == 0)
        {
            var window = new VrControlPanelWindow(new VrControlPanelState(
                VoiceEnabled: true,
                TranslationEnabled: true,
                SendImmediately: true,
                DisplayMode: VoiceTranslationDisplayModes.OriginalThenTranslation,
                TargetLanguage: "zh-CN",
                ChunkIntervalMilliseconds: 1000,
                CaptureEye: "left-eye"))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false
            };
            return (
                window,
                new WpfSpatialOverlayOptions
                {
                    Name = $"Stress Control Panel {windowIndex + 1}",
                    WidthMeters = 0.25f,
                    Placement = WpfSpatialOverlayPlacement.Head,
                    CanGrab = true,
                    ShowToolbarWhenGrabbed = false,
                    MaximumFramesPerSecond = 60
                });
        }

        var history = new SubtitleHistoryViewModel(configuration.Subtitles);
        var entry = history.Add(
            windowIndex,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(3),
            $"SteamVR Overlay stress window {windowIndex + 1}");
        entry.TranslatedText = $"SteamVR 叠加层压力窗口 {windowIndex + 1}";
        entry.IsTranslating = false;
        var subtitle = new SubtitleHistoryWindow(history)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false
        };
        return (
            subtitle,
            MainWindow.CreateSubtitleOverlayOptions(
                configuration.Subtitles,
                WpfSpatialOverlayPlacement.Head) with
            {
                Name = $"Stress Subtitle Window {windowIndex + 1}"
            });
    }

    private static async Task<IReadOnlyList<WpfOverlayFrameRenderedEventArgs>> MeasurePhaseAsync(
        SteamVrTranslationRuntime runtime,
        ConcurrentQueue<WpfOverlayFrameRenderedEventArgs> frames,
        long overlayId,
        CancellationToken cancellationToken)
    {
        Drain(frames);
        var stopwatch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(InvalidationInterval);
        while (stopwatch.Elapsed < PhaseDuration &&
               await timer.WaitForNextTickAsync(cancellationToken))
        {
            runtime.InvalidateWindow(overlayId);
        }
        await Task.Delay(100, cancellationToken);
        return Drain(frames);
    }

    private static async Task<IReadOnlyList<WpfOverlayFrameRenderedEventArgs>> MeasureCombinedPhaseAsync(
        SteamVrTranslationRuntime runtime,
        ConcurrentQueue<WpfOverlayFrameRenderedEventArgs> frames,
        long firstOverlayId,
        long secondOverlayId,
        CancellationToken cancellationToken)
    {
        Drain(frames);
        var stopwatch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(InvalidationInterval);
        while (stopwatch.Elapsed < PhaseDuration &&
               await timer.WaitForNextTickAsync(cancellationToken))
        {
            runtime.InvalidateWindow(firstOverlayId);
            runtime.InvalidateWindow(secondOverlayId);
        }
        await Task.Delay(100, cancellationToken);
        return Drain(frames);
    }

    private static async Task<OverlayStressMeasurement> MeasureManyAsync(
        SteamVrTranslationRuntime runtime,
        ConcurrentQueue<WpfOverlayFrameRenderedEventArgs> frames,
        IReadOnlyList<long> overlayIds,
        TimeSpan phaseDuration,
        CancellationToken cancellationToken)
    {
        Drain(frames);
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(0x5A17 + overlayIds.Count);
        var nextPointerMoveAt = TimeSpan.Zero;
        var pointerMoveRequests = 0;
        using var timer = new PeriodicTimer(InvalidationInterval);
        while (stopwatch.Elapsed < phaseDuration &&
               await timer.WaitForNextTickAsync(cancellationToken))
        {
            foreach (var overlayId in overlayIds)
            {
                runtime.InvalidateWindow(overlayId);
            }
            if (stopwatch.Elapsed >= nextPointerMoveAt)
            {
                var target = overlayIds[random.Next(overlayIds.Count)];
                var point = new NormalizedPoint(
                    0.06f + (0.88f * random.NextSingle()),
                    0.06f + (0.88f * random.NextSingle()));
                runtime.MoveWindowPointerForDiagnostics(
                    target,
                    point,
                    isPressed: pointerMoveRequests % 45 == 44);
                pointerMoveRequests++;
                nextPointerMoveAt += TimeSpan.FromSeconds(1d / 60d);
            }
        }
        await Task.Delay(100, cancellationToken);
        return new OverlayStressMeasurement(Drain(frames), pointerMoveRequests);
    }

    private static async Task<OverlayRealisticMeasurement> MeasureRealisticPhaseAsync(
        SteamVrTranslationRuntime runtime,
        ConcurrentQueue<WpfOverlayFrameRenderedEventArgs> renderedFrames,
        ConcurrentQueue<ResultOverlayFrameRenderedEventArgs> resultFrames,
        ConcurrentQueue<DiagnosticRuntimeFrameCompletedEventArgs> runtimeFrames,
        ConcurrentQueue<DiagnosticWpfPointerProcessedEventArgs> pointerEvents,
        ConcurrentQueue<DiagnosticWpfInteractionCompletedEventArgs> interactionEvents,
        IReadOnlyList<RealisticWindowContext> contexts,
        TimeSpan phaseDuration,
        CancellationToken cancellationToken)
    {
        Drain(renderedFrames);
        Drain(resultFrames);
        Drain(runtimeFrames);
        Drain(pointerEvents);
        Drain(interactionEvents);
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(0x7E41 + contexts.Count);
        var controlContexts = contexts
            .Where(item => item.Kind == RealisticWindowKind.ControlPanel)
            .ToArray();
        var subtitleContexts = contexts
            .Where(item => item.Kind == RealisticWindowKind.Subtitle)
            .ToArray();
        var markdownContexts = contexts
            .Where(item => item.Kind == RealisticWindowKind.Markdown)
            .ToArray();
        var chatContexts = contexts
            .Where(item => item.Kind == RealisticWindowKind.Chat)
            .ToArray();
        var pointerContexts = contexts
            .Where(item => item.Window is not null)
            .ToArray();
        foreach (var context in subtitleContexts)
        {
            context.NextStreamUpdateAt = TimeSpan.FromMilliseconds(random.Next(20, 250));
            context.NextSubtitleEntryAt = TimeSpan.FromMilliseconds(random.Next(800, 1500));
        }

        var nextPointerMoveAt = TimeSpan.Zero;
        var nextClickAt = TimeSpan.FromMilliseconds(250);
        var nextSubtitleScrollAt = TimeSpan.FromMilliseconds(300);
        var nextMarkdownScrollAt = TimeSpan.Zero;
        var nextMarkdownDirectionChangeAt = TimeSpan.FromMilliseconds(1200);
        var nextChatUpdateAt = TimeSpan.Zero;
        var pointerMoveRequests = 0;
        var uiClickRequests = 0;
        var subtitleScrollRequests = 0;
        var markdownScrollRequests = 0;
        var chatUpdateRequests = 0;
        var clickTargetIndex = 0;
        var scrollTargetIndex = 0;
        var subtitleScrollDirection = 1;
        var markdownScrollDirection = 1;
        using var timer = new PeriodicTimer(InvalidationInterval);
        while (stopwatch.Elapsed < phaseDuration &&
               await timer.WaitForNextTickAsync(cancellationToken))
        {
            var elapsed = stopwatch.Elapsed;
            if (pointerContexts.Length > 0 && elapsed >= nextPointerMoveAt)
            {
                var target = pointerContexts[random.Next(pointerContexts.Length)];
                var point = new NormalizedPoint(
                    0.06f + (0.88f * random.NextSingle()),
                    0.06f + (0.88f * random.NextSingle()));
                if (runtime.MoveWindowPointerForDiagnostics(
                        target.OverlayId,
                        point,
                        isPressed: false) != 0)
                {
                    pointerMoveRequests++;
                }
                nextPointerMoveAt += TimeSpan.FromSeconds(1d / 60d);
            }

            if (controlContexts.Length > 0 && elapsed >= nextClickAt)
            {
                var target = controlContexts[clickTargetIndex++ % controlContexts.Length];
                var controlName = ControlInteractionSequence[
                    target.NextControlInteractionIndex++ % ControlInteractionSequence.Length];
                if (runtime.ClickWindowControlForDiagnostics(target.OverlayId, controlName) != 0)
                {
                    uiClickRequests++;
                }
                nextClickAt += TimeSpan.FromMilliseconds(650);
            }

            if (subtitleContexts.Length > 0 && elapsed >= nextSubtitleScrollAt)
            {
                var target = subtitleContexts[scrollTargetIndex++ % subtitleContexts.Length];
                if (runtime.ScrollWindowForDiagnostics(
                        target.OverlayId,
                        new NormalizedPoint(0.5f, 0.58f),
                        subtitleScrollDirection * 240) != 0)
                {
                    subtitleScrollRequests++;
                }
                subtitleScrollDirection *= -1;
                nextSubtitleScrollAt += TimeSpan.FromMilliseconds(350);
            }

            if (markdownContexts.Length > 0 && elapsed >= nextMarkdownScrollAt)
            {
                if (elapsed >= nextMarkdownDirectionChangeAt)
                {
                    markdownScrollDirection *= -1;
                    nextMarkdownDirectionChangeAt += TimeSpan.FromMilliseconds(1200);
                }
                var targetIndex = (int)(elapsed.TotalMilliseconds / 1500d) % markdownContexts.Length;
                if (runtime.ScrollResultForDiagnostics(
                        markdownContexts[targetIndex].OverlayId,
                        markdownScrollDirection * 9d) != 0)
                {
                    markdownScrollRequests++;
                }
                nextMarkdownScrollAt += TimeSpan.FromSeconds(1d / 60d);
            }

            if (chatContexts.Length > 0 && elapsed >= nextChatUpdateAt)
            {
                var targetIndex = (int)(elapsed.TotalMilliseconds / 1500d) % chatContexts.Length;
                var target = chatContexts[targetIndex];
                target.ChatRevision++;
                if (runtime.UpdateChatResultForDiagnostics(
                        target.OverlayId,
                        CreateRealisticChatView(target.ChatHistory!, target.ChatRevision)) != 0)
                {
                    chatUpdateRequests++;
                }
                nextChatUpdateAt += TimeSpan.FromMilliseconds(100);
            }

            foreach (var target in subtitleContexts)
            {
                if (elapsed >= target.NextStreamUpdateAt && target.ActiveSubtitleEntry is { } active)
                {
                    target.StreamRevision++;
                    active.IsTranslating = true;
                    active.TranslatedText =
                        $"流式翻译窗口 {target.OverlayId}，片段 {target.StreamRevision:D3}：" +
                        "字幕内容正在持续更新，用于测量文本重排和纹理上传延迟。";
                    target.NextStreamUpdateAt += TimeSpan.FromMilliseconds(250);
                }

                if (elapsed >= target.NextSubtitleEntryAt && target.History is { } history)
                {
                    if (target.ActiveSubtitleEntry is { } previous)
                    {
                        previous.IsTranslating = false;
                    }
                    var entryIndex = target.SubtitleEntryCount++;
                    var start = TimeSpan.FromSeconds(entryIndex * 3);
                    target.ActiveSubtitleEntry = history.Add(
                        entryIndex % 4,
                        start,
                        start + TimeSpan.FromSeconds(3),
                        $"Live speaker {entryIndex % 4 + 1}: newly recognized subtitle segment {entryIndex + 1}.");
                    target.ActiveSubtitleEntry.IsTranslating = true;
                    target.NextSubtitleEntryAt += TimeSpan.FromMilliseconds(1500);
                }
            }
        }

        await Task.Delay(100, cancellationToken);
        return new OverlayRealisticMeasurement(
            Drain(renderedFrames),
            Drain(resultFrames),
            Drain(runtimeFrames),
            Drain(pointerEvents),
            Drain(interactionEvents),
            pointerMoveRequests,
            uiClickRequests,
            subtitleScrollRequests,
            markdownScrollRequests,
            chatUpdateRequests);
    }

    private static List<WpfOverlayFrameRenderedEventArgs> Drain(
        ConcurrentQueue<WpfOverlayFrameRenderedEventArgs> frames)
    {
        var result = new List<WpfOverlayFrameRenderedEventArgs>();
        while (frames.TryDequeue(out var frame))
        {
            result.Add(frame);
        }
        return result;
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> queue)
    {
        var result = new List<T>();
        while (queue.TryDequeue(out var item))
        {
            result.Add(item);
        }
        return result;
    }

    private static OverlayPhaseReport Summarize(
        IReadOnlyList<WpfOverlayFrameRenderedEventArgs> frames,
        long overlayId)
    {
        var selected = frames
            .Where(frame => frame.OverlayId == overlayId)
            .OrderBy(frame => frame.RenderedAt)
            .ToArray();
        var measuredDuration = selected.Length > 1
            ? selected[^1].RenderedAt - selected[0].RenderedAt + TimeSpan.FromSeconds(1d / 60d)
            : PhaseDuration;
        var effectiveFps = selected.Length / Math.Max(0.001, measuredDuration.TotalSeconds);
        return new OverlayPhaseReport(
            selected.Length,
            effectiveFps,
            Percentile(selected.Select(frame => frame.RasterizeDuration.TotalMilliseconds), 0.95),
            Percentile(selected.Select(frame => frame.UploadDuration.TotalMilliseconds), 0.95),
            Percentile(selected.Select(frame => frame.TotalDuration.TotalMilliseconds), 0.95));
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 1_000_000_000d;
        }
        var index = (int)Math.Ceiling((ordered.Length - 1) * percentile);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static double PercentileOrZero(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }
        var index = (int)Math.Ceiling((ordered.Length - 1) * percentile);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static double EffectiveRuntimeFramesPerSecond(
        IReadOnlyList<DiagnosticRuntimeFrameCompletedEventArgs> frames)
    {
        if (frames.Count < 2)
        {
            return 0;
        }

        var ordered = frames.OrderBy(frame => frame.CompletedTimestamp).ToArray();
        var elapsedSeconds =
            (ordered[^1].CompletedTimestamp - ordered[0].CompletedTimestamp) /
            (double)Stopwatch.Frequency;
        return (ordered.Length - 1) / Math.Max(0.001, elapsedSeconds);
    }

    private static double AngularSizeDegrees(double sizeMeters, double distanceMeters) =>
        2d * Math.Atan(sizeMeters / 2d / distanceMeters) * 180d / Math.PI;

    private static IReadOnlyList<double> RuntimeFrameIntervals(
        IReadOnlyList<DiagnosticRuntimeFrameCompletedEventArgs> frames)
    {
        var ordered = frames.OrderBy(frame => frame.CompletedTimestamp).ToArray();
        var result = new List<double>(Math.Max(0, ordered.Length - 1));
        for (var index = 1; index < ordered.Length; index++)
        {
            result.Add(
                (ordered[index].CompletedTimestamp - ordered[index - 1].CompletedTimestamp) *
                1000d /
                Stopwatch.Frequency);
        }
        return result;
    }

    private static double EffectivePointerUpdatesPerSecond(
        IReadOnlyList<DiagnosticWpfPointerProcessedEventArgs> events)
    {
        if (events.Count < 2)
        {
            return 0;
        }

        var ordered = events.OrderBy(item => item.ProcessedTimestamp).ToArray();
        var elapsedSeconds =
            (ordered[^1].ProcessedTimestamp - ordered[0].ProcessedTimestamp) /
            (double)Stopwatch.Frequency;
        return (ordered.Length - 1) / Math.Max(0.001, elapsedSeconds);
    }

    private static string ResolveOutputPath(string[] args)
    {
        var argument = args.FirstOrDefault(value => value.StartsWith("--output=", StringComparison.OrdinalIgnoreCase));
        var path = argument?["--output=".Length..];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppContext.BaseDirectory, "overlay-benchmark.json")
            : path);
    }

    private static int ResolveIntArgument(
        string[] args,
        string prefix,
        int fallback,
        int minimum,
        int maximum)
    {
        var value = args.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return int.TryParse(value?[prefix.Length..], out var parsed)
            ? Math.Clamp(parsed, minimum, maximum)
            : fallback;
    }

    private static string ResolveStringArgument(string[] args, string prefix)
    {
        var value = args.FirstOrDefault(argument =>
            argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value?[prefix.Length..].Trim() ?? string.Empty;
    }

    private static int[] ResolveIntListArgument(
        string[] args,
        string prefix,
        int[] fallback)
    {
        var value = ResolveStringArgument(args, prefix);
        var parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var number) ? number : 0)
            .Where(item => item > 0)
            .Distinct()
            .ToArray();
        return parsed.Length == 0 ? fallback : parsed;
    }

    private static string[] ResolveStringListArgument(
        string[] args,
        string prefix,
        string[] fallback)
    {
        var value = ResolveStringArgument(args, prefix);
        var parsed = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return parsed.Length == 0 ? fallback : parsed;
    }
}

internal sealed record AndroidMirrorBenchmarkReport(
    int TargetFramesPerSecond,
    int SourceTargetFramesPerSecond,
    string DeviceSerial,
    int PhaseSeconds,
    IReadOnlyList<AndroidMirrorStageReport> Stages,
    bool Passed);

internal sealed record AndroidStreamBenchmarkReport(
    string DeviceSerial,
    int PhaseSeconds,
    IReadOnlyList<AndroidStreamStageReport> Stages,
    bool Passed);

internal sealed record AndroidStreamStageReport(
    string Mode,
    int MaximumSize,
    int RequestedFramesPerSecond,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int ActualWidth,
    int ActualHeight,
    long EncodedFrames,
    double EncodedFramesPerSecond,
    long DecodedFrames,
    double DecodedFramesPerSecond,
    long ConvertedFrames,
    double ConvertedFramesPerSecond,
    double AveragePixelConversionMilliseconds,
    double AverageHardwareTransferMilliseconds,
    int TouchRequests,
    double ProcessCpuCoreEquivalentPercent,
    double ProcessCpuTotalCapacityPercent,
    double ProcessWorkingSetMiB);

internal sealed record AndroidMirrorStageReport(
    int MaximumSize,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int ActualWidth,
    int ActualHeight,
    double PhysicalWidthMeters,
    double PhysicalHeightMeters,
    double DistanceMeters,
    double HorizontalAngularSizeDegrees,
    double VerticalAngularSizeDegrees,
    long PhoneFrames,
    double PhoneFramesPerSecond,
    int TextureUploads,
    double TextureUploadsPerSecond,
    double RuntimeLoopFramesPerSecond,
    double P95RuntimeWorkMilliseconds,
    double P95RuntimeFrameIntervalMilliseconds,
    int PointerMoveRequests,
    int SourceTouchRequests,
    int OpenVrScreenTouchEventsQueued,
    bool OpenVrScreenTouchPassed,
    bool OpenVrEdgeConstraintPassed,
    int PointerMovesProcessed,
    double PointerUpdatesPerSecond,
    double P95PointerQueueMilliseconds,
    double P95RasterizeMilliseconds,
    double P95UploadMilliseconds,
    double P95TotalMilliseconds,
    double ProcessCpuCoreEquivalentPercent,
    double ProcessCpuTotalCapacityPercent,
    double ProcessWorkingSetMiB);

internal sealed record AndroidOpenVrInteractionCheck(
    int ScreenTouchEventsQueued,
    bool ScreenTouchPassed,
    bool EdgeConstraintPassed);

internal sealed record OverlayBenchmarkReport(
    int TargetFramesPerSecond,
    int IdleContentUploads,
    OverlayPhaseReport ControlPanel,
    OverlayPhaseReport SubtitleWindow,
    OverlayPhaseReport CombinedControlPanel,
    OverlayPhaseReport CombinedSubtitleWindow,
    double CombinedP95FrameMilliseconds,
    IReadOnlyList<OpenVrPointerCheckReport> OpenVrPointerChecks,
    bool OpenVrPointerLeaveCleared);

internal sealed record OpenVrPointerCheckReport(
    string Target,
    NormalizedPoint RequestedTexturePoint,
    NormalizedPoint? ResolvedTexturePoint,
    string? HoveredControlName,
    double ErrorPixels,
    bool Passed,
    bool LeftWindow);

internal sealed record OverlayPhaseReport(
    int Frames,
    double EffectiveFramesPerSecond,
    double P95RasterizeMilliseconds,
    double P95UploadMilliseconds,
    double P95TotalMilliseconds);

internal sealed record OverlayStressReport(
    int TargetFramesPerSecond,
    double PassingThresholdFramesPerSecond,
    int TextureLongEdge,
    int PhaseSeconds,
    int RequestedMaximumWindows,
    int MaximumSustainedWindowCount,
    int? FirstWindowCountBelowTarget,
    IReadOnlyList<OverlayStressStageReport> Stages);

internal sealed record OverlayStressStageReport(
    int WindowCount,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int TotalFrames,
    double MinimumFramesPerSecond,
    double AverageFramesPerSecond,
    int RandomPointerMoveRequests,
    double ProcessCpuCoreEquivalentPercent,
    double ProcessCpuTotalCapacityPercent,
    double ProcessWorkingSetMiB,
    double CombinedP95FrameMilliseconds,
    IReadOnlyList<OverlayPhaseReport> Windows);

internal sealed record OverlayStressMeasurement(
    IReadOnlyList<WpfOverlayFrameRenderedEventArgs> Frames,
    int RandomPointerMoveRequests);

internal sealed record OverlayRealisticStressReport(
    int TargetFramesPerSecond,
    double PassingThresholdFramesPerSecond,
    int TextureLongEdge,
    int PhaseSeconds,
    int RequestedMaximumWindows,
    int MaximumSustainedWindowCount,
    int? FirstWindowCountBelowTarget,
    IReadOnlyList<string> Workload,
    IReadOnlyList<OverlayRealisticStageReport> Stages);

internal sealed record OverlayRealisticStageReport(
    int WindowCount,
    int ControlPanelCount,
    int SubtitleWindowCount,
    int MarkdownWindowCount,
    int ChatWindowCount,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int RuntimeLoopFrames,
    double RuntimeLoopFramesPerSecond,
    double P95RuntimeWorkMilliseconds,
    double P95RuntimeFrameIntervalMilliseconds,
    double P99RuntimeFrameIntervalMilliseconds,
    int RuntimeDeadlineMisses,
    double RuntimeDeadlineMissPercent,
    int WpfContentUploads,
    double P95WpfRasterizeMilliseconds,
    double P95WpfUploadMilliseconds,
    double P95WpfTotalMilliseconds,
    int ControlPanelContentUploads,
    int SubtitleContentUploads,
    double SubtitleContentUpdatesPerSecond,
    double P95SubtitleRasterizeMilliseconds,
    int MarkdownContentUploads,
    double MarkdownContentUpdatesPerSecond,
    double P95MarkdownRasterizeMilliseconds,
    double P95MarkdownUploadMilliseconds,
    double P95MarkdownTotalMilliseconds,
    int ChatContentUploads,
    double ChatContentUpdatesPerSecond,
    double P95ChatRasterizeMilliseconds,
    double P95ChatUploadMilliseconds,
    double P95ChatTotalMilliseconds,
    int PointerMoveRequests,
    int PointerMovesProcessed,
    double PointerUpdatesPerSecond,
    double P95PointerQueueMilliseconds,
    int UiClickRequests,
    int SubtitleScrollRequests,
    int MarkdownScrollRequests,
    int ChatUpdateRequests,
    int InteractionCompletions,
    int InteractionFailures,
    double P50InteractionToTextureMilliseconds,
    double P95InteractionToTextureMilliseconds,
    double MaximumInteractionToTextureMilliseconds,
    double ProcessCpuCoreEquivalentPercent,
    double ProcessCpuTotalCapacityPercent,
    double ProcessWorkingSetMiB);

internal sealed record OverlayRealisticMeasurement(
    IReadOnlyList<WpfOverlayFrameRenderedEventArgs> RenderedFrames,
    IReadOnlyList<ResultOverlayFrameRenderedEventArgs> ResultFrames,
    IReadOnlyList<DiagnosticRuntimeFrameCompletedEventArgs> RuntimeFrames,
    IReadOnlyList<DiagnosticWpfPointerProcessedEventArgs> PointerEvents,
    IReadOnlyList<DiagnosticWpfInteractionCompletedEventArgs> Interactions,
    int PointerMoveRequests,
    int UiClickRequests,
    int SubtitleScrollRequests,
    int MarkdownScrollRequests,
    int ChatUpdateRequests);

internal enum RealisticWindowKind
{
    ControlPanel,
    Subtitle,
    Markdown,
    Chat
}

internal sealed class RealisticWindowContext(
    Window? window,
    WpfSpatialOverlayOptions? options,
    RealisticWindowKind kind,
    SubtitleHistoryViewModel? history)
{
    public Window? Window { get; } = window;

    public WpfSpatialOverlayOptions? Options { get; } = options;

    public RealisticWindowKind Kind { get; } = kind;

    public SubtitleHistoryViewModel? History { get; } = history;

    public long OverlayId { get; set; }

    public int NextControlInteractionIndex { get; set; }

    public SubtitleHistoryEntry? ActiveSubtitleEntry { get; set; }

    public int SubtitleEntryCount { get; set; } = 8;

    public int StreamRevision { get; set; }

    public TimeSpan NextStreamUpdateAt { get; set; }

    public TimeSpan NextSubtitleEntryAt { get; set; }

    public IReadOnlyList<AssistantConversationTurn>? ChatHistory { get; init; }

    public int ChatRevision { get; set; }

    public static RealisticWindowContext CreateMarkdown() =>
        new(null, null, RealisticWindowKind.Markdown, null);

    public static RealisticWindowContext CreateChat(
        IReadOnlyList<AssistantConversationTurn> history) =>
        new(null, null, RealisticWindowKind.Chat, null)
        {
            ChatHistory = history
        };
}
