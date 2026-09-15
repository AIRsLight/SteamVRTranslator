using System.Buffers.Binary;
using System.IO;
using NAudio.Wave;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SubtitleAudioDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subtitle-diagnostics-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _lines = [];
    private readonly SubtitleAudioDiagnostics _diagnostics;

    public SubtitleAudioDiagnosticsTests()
    {
        var log = new AppLog(_directory);
        log.MessageWritten += (_, line) => _lines.Add(line);
        _diagnostics = new SubtitleAudioDiagnostics(log, "[subtitles] [session=test] [capture=1]");
    }

    [Fact]
    public void NoPacketsAndSilentPacketsHaveDifferentDiagnosticsAndAreRateLimited()
    {
        for (var ms = 0; ms < 5000; ms += 100) _diagnostics.Report(TimeSpan.FromMilliseconds(ms));
        Assert.Empty(_lines);
        _diagnostics.Report(TimeSpan.FromSeconds(5));
        Assert.Contains("packets=0", Assert.Single(_lines));
        Assert.Contains("lastPacketAgoMs=none", _lines[0]);

        for (var ms = 5100; ms <= 10000; ms += 100)
        {
            _diagnostics.Record(new byte[400], 100, 2, TimeSpan.FromMilliseconds(ms));
            _diagnostics.Report(TimeSpan.FromMilliseconds(ms));
        }
        Assert.Equal(2, _lines.Count);
        Assert.Contains("status=first-packet", _lines[1]);
        Assert.Contains("silentPackets=1 audiblePackets=0", _lines[1]);
        _diagnostics.Report(TimeSpan.FromMilliseconds(10100));
        Assert.Equal(3, _lines.Count);
        Assert.Contains("packets=50 frames=5000 silentPackets=50 audiblePackets=0", _lines[2]);
        Assert.Contains("rms=0.000000 peak=0.000000", _lines[2]);
    }

    [Fact]
    public void HeartbeatsReportLevelAndDiscontinuitiesThenClearTheMeasurementWindow()
    {
        var bytes = Pcm(100, -16384);
        Assert.Equal(0.5, _diagnostics.Record(bytes, 100, 5, TimeSpan.FromMilliseconds(20)));
        _diagnostics.Report(TimeSpan.FromMilliseconds(20));
        Assert.Contains("audiblePackets=1 discontinuities=1 timestampErrors=1", Assert.Single(_lines));
        Assert.Contains("rms=0.500000 peak=0.500000", _lines[0]);
        _diagnostics.Report(TimeSpan.FromMilliseconds(5020));
        Assert.Contains("windowSamples=0 rms=0.000000 peak=0.000000", _lines[1]);
        Assert.Contains("lastPacketAgoMs=5000", _lines[1]);
        _diagnostics.Report(TimeSpan.FromMilliseconds(5100), final: true);
        Assert.Contains("status=final", _lines[2]);
    }

    [Theory]
    [InlineData("ending-silence")]
    [InlineData("maximum-duration")]
    [InlineData("packet-gap")]
    [InlineData("capture-stop")]
    public void SegmentDiagnosticsPreserveTheTriggerAndAudio(string reason)
    {
        var segmenter = new ProcessLoopbackAudioCapture.SpeechSegmenter();
        var format = new WaveFormat(1000, 16, 2);
        ProcessLoopbackAudioSegment? segment = null;
        if (reason == "maximum-duration")
        {
            for (var index = 0; index < 120; index++) segment = segmenter.Push(Pcm(100, 1000), 100, format);
        }
        else
        {
            for (var index = 0; index < 5; index++) Assert.Null(segmenter.Push(Pcm(100, 1000), 100, format));
            if (reason == "ending-silence")
                for (var index = 0; index < 7; index++) segment = segmenter.Push(Pcm(100, 0), 100, format);
            else segment = segmenter.Flush(format, reason);
        }
        Assert.NotNull(segment);
        Assert.Equal(reason, segment.CompletionReason);
        Assert.Equal((segment.End - segment.Start).TotalSeconds * format.AverageBytesPerSecond, segment.PcmBytes.Length);
    }

    [Fact]
    public void VeryShortAudioReportsWhyItWasDiscarded()
    {
        var discarded = new List<(string Reason, TimeSpan Duration)>();
        var segmenter = new ProcessLoopbackAudioCapture.SpeechSegmenter((reason, duration) => discarded.Add((reason, duration)));
        var format = new WaveFormat(1000, 16, 2);
        Assert.Null(segmenter.Push(Pcm(100, 1000), 100, format));
        Assert.Null(segmenter.Flush(format, "packet-gap"));
        Assert.Equal(("packet-gap", TimeSpan.FromMilliseconds(100)), Assert.Single(discarded));
        Assert.Null(segmenter.Flush(format, "capture-stop"));
        Assert.Single(discarded);
    }

    private static byte[] Pcm(int frames, short amplitude)
    {
        var bytes = new byte[frames * 4];
        for (var index = 0; index < bytes.Length; index += 2)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index), amplitude);
        return bytes;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
