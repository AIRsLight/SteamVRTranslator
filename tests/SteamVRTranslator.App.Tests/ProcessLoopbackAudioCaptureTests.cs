using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class ProcessLoopbackAudioCaptureTests
{
    [Fact]
    public async Task CapturesAudioRenderedByTheTargetProcessWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SVT_RUN_AUDIO_CAPTURE_TEST"),
                "1",
                StringComparison.Ordinal) ||
            !ProcessLoopbackAudioCapture.IsSupported)
        {
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"svt-process-loopback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var log = new AppLog(directory);
            var segmentReady = new TaskCompletionSource<ProcessLoopbackAudioSegment>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var capture = new ProcessLoopbackAudioCapture(
                unchecked((uint)Environment.ProcessId),
                log);
            capture.SegmentReady += (_, segment) => segmentReady.TrySetResult(segment);
            await capture.StartAsync(CancellationToken.None);

            using var output = new WaveOutEvent();
            var tone = new SignalGenerator(44100, 2)
            {
                Frequency = 440,
                Gain = 0.2,
                Type = SignalGeneratorType.Sin
            };
            output.Init(new OffsetSampleProvider(tone)
            {
                Take = TimeSpan.FromSeconds(1)
            });
            output.Play();
            await Task.Delay(TimeSpan.FromSeconds(1.25));
            await capture.DisposeAsync();

            var segment = await segmentReady.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(segment.PcmBytes.Length > segment.Format.AverageBytesPerSecond / 2);
            Assert.True(segment.End > segment.Start);
            Assert.Contains(
                "已开始捕获进程音频",
                await File.ReadAllTextAsync(log.FilePath));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
