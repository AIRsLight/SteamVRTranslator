using System.IO;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Output;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class CustomVoiceInputCueTests
{
    [Fact]
    public void ImportedMonoAudioIsPortableAndResampledWithoutDependingOnOriginal()
    {
        using var files = new AudioFiles();
        var original = files.Write("按下.wav", 22050, 1, 0.1, 0.25f);
        var application = Path.Combine(files.DirectoryPath, "app");
        var relative = VoiceInputCueAudio.Import(original, application);
        Assert.False(Path.IsPathFullyQualified(relative));
        Assert.Equal("按下.wav", Path.GetFileName(relative));
        File.Delete(original);
        var imported = Path.Combine(application, relative);
        using (var reader = new WaveFileReader(imported))
        {
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
            Assert.Equal(2, reader.WaveFormat.Channels);
        }
        var movedApplication = Path.Combine(files.DirectoryPath, "moved-app");
        Directory.Move(application, movedApplication);
        var sounds = VoiceInputCueSounds.Load(new VoiceInputCueConfiguration { StartFilePath = relative }, movedApplication);
        var clip = Assert.IsType<VoiceInputCueClip>(sounds.Start);
        Assert.InRange(clip.FrameCount, 4790, 4810);
        Assert.Equal(0.25f, clip.Sample(2400, 0), 3);
        Assert.Equal(clip.Sample(2400, 0), clip.Sample(2400, 1));
    }

    [Fact]
    public void CustomStagesPreserveStereoGainAndLoopOnlyTheRecordingSound()
    {
        var configuration = new VoiceInputCueConfiguration { VolumePercent = 50 };
        var sounds = new VoiceInputCueSounds(Clip(100, 0.2f, -0.4f), Clip(80, 0.6f, -0.8f), Clip(4000, 0.4f, -0.2f));
        var source = new VoiceInputCueSampleProvider(configuration, sounds);
        Assert.Equal(TimeSpan.FromMilliseconds(100), source.OpeningDuration);
        Assert.Equal(TimeSpan.FromSeconds(7), source.EndingTimeout);
        var opening = Read(source, 100);
        Assert.Equal(0.1f, opening[1200]);
        Assert.Equal(-0.2f, opening[1201]);
        var loop = Read(source, 80);
        Assert.Equal(0.3f, loop[1200]);
        Assert.Equal(-0.4f, loop[1201]);
        Assert.Equal(loop, Read(source, 80));
        source.End();
        var closing = Read(source, 4010);
        Assert.Equal(4010 * 96, closing.Length);
        Assert.Equal(0.2f, closing[2160]);
        Assert.Equal(-0.1f, closing[2161]);
        Assert.Equal(0f, closing[^1]);
        Assert.Empty(Read(source, 10));
    }

    [Fact]
    public void ReleasingPttInterruptsALongCustomOpeningAndDisabledStagesStaySilent()
    {
        var sounds = new VoiceInputCueSounds(Clip(10000, 0.5f, 0.5f), Clip(80, 0.8f, 0.8f), Clip(50, 0.2f, 0.2f));
        var source = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration { VolumePercent = 100 }, sounds);
        Read(source, 20);
        source.End();
        var ending = Read(source, 1000);
        Assert.Equal(60 * 96, ending.Length);
        Assert.Equal(0.2f, ending[2160]);
        source.End();
        Assert.Empty(Read(source, 20));
        var silent = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration
        {
            StartEnabled = false, OngoingEnabled = false, EndEnabled = false
        }, sounds);
        Assert.All(Read(silent, 100), sample => Assert.Equal(0f, sample));
        silent.End();
        Assert.Equal(10 * 96, Read(silent, 100).Length);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("empty")]
    [InlineData("too-long")]
    [InlineData("too-large")]
    [InlineData("surround")]
    [InlineData("non-finite")]
    [InlineData("unsupported")]
    public void InvalidImportsDoNotCreateFilesOrOverwriteExistingSelection(string kind)
    {
        using var files = new AudioFiles();
        var valid = files.Write("valid.wav", 48000, 2, 0.05, 0.2f);
        var application = Path.Combine(files.DirectoryPath, "app");
        var previous = VoiceInputCueAudio.Import(valid, application);
        var previousBytes = File.ReadAllBytes(Path.Combine(application, previous));
        var invalid = Path.Combine(files.DirectoryPath, "invalid.wav");
        switch (kind)
        {
            case "empty": files.Write("invalid.wav", 48000, 2, 0, 0); break;
            case "too-long": files.Write("invalid.wav", 48000, 2, 30.01, 0); break;
            case "surround": files.Write("invalid.wav", 48000, 6, 0.01, 0); break;
            case "non-finite": files.Write("invalid.wav", 48000, 2, 0.01, float.NaN); break;
            case "too-large":
                using (var stream = File.Create(invalid)) stream.SetLength(VoiceInputCueAudio.MaximumFileBytes + 1);
                break;
            case "unsupported": invalid = Path.Combine(files.DirectoryPath, "invalid.txt"); goto default;
            default: File.WriteAllText(invalid, "not audio"); break;
        }
        Assert.Throws<InvalidDataException>(() => VoiceInputCueAudio.Import(invalid, application));
        Assert.Equal(previousBytes, File.ReadAllBytes(Path.Combine(application, previous)));
        Assert.Single(Directory.GetFiles(application, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void MissingFilesFailPreviewButRuntimeFallsBackOnlyForTheBrokenStage()
    {
        using var files = new AudioFiles();
        var path = files.Write("valid.wav", 48000, 2, 0.1, 0.4f);
        var configuration = new VoiceInputCueConfiguration { StartFilePath = "missing.wav", EndFilePath = path };
        Assert.Throws<InvalidDataException>(() => VoiceInputCueSounds.Load(configuration, files.DirectoryPath));
        var log = new AppLog(files.DirectoryPath);
        var sounds = VoiceInputCueSounds.Load(configuration, files.DirectoryPath, log);
        Assert.Null(sounds.Start);
        Assert.NotNull(sounds.End);
        Assert.Contains("missing.wav", File.ReadAllText(log.FilePath));
        configuration.StartEnabled = false;
        Assert.NotNull(VoiceInputCueSounds.Load(configuration, files.DirectoryPath).End);
    }

    [Fact]
    public async Task InvalidFilePreviewFailsBeforeOpeningAnAudioDevice()
    {
        using var files = new AudioFiles();
        var opened = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceInputCuePreview.PlayAsync(
            new VoiceInputCueConfiguration { StartFilePath = Path.Combine(files.DirectoryPath, "missing.wav") },
            "headphones", CancellationToken.None, (id, source) =>
            {
                opened = true;
                return new Playback(source);
            }));
        Assert.False(opened);
    }

    [Fact]
    public void GameAndEchoUseIndependentCursorsFromTheSamePreloadedCustomAudio()
    {
        using var files = new AudioFiles();
        var original = files.Write("custom.wav", 48000, 2, 0.1, 0.3f);
        var configuration = new VoiceInputCueConfiguration
        {
            Enabled = true, EchoEnabled = true, StartFilePath = original, OngoingFilePath = original, EndFilePath = original
        };
        var playbacks = new List<Playback>();
        using var output = new VoiceInputCueOutput(configuration, new AppLog(files.DirectoryPath), (_, source) =>
        {
            var playback = new Playback(source);
            playbacks.Add(playback);
            return playback;
        }, () => new VbCableStatus(true, "game", "capture"), () => "headphones");
        File.Delete(original);
        Assert.True(output.Start());
        Assert.Equal(2, playbacks.Count);
        var game = Assert.IsType<VoiceInputCueSampleProvider>(playbacks[0].Source);
        var echo = Assert.IsType<VoiceInputCueSampleProvider>(playbacks[1].Source);
        Assert.NotSame(game, echo);
        Assert.Same(game.Sounds, echo.Sounds);
        Assert.Equal(Read(game, 300), Read(echo, 300));
        output.EndAsync();
        Assert.Equal(Read(game, 110), Read(echo, 110));
        output.Cancel();
        Assert.All(playbacks, playback => Assert.True(playback.Disposed));
    }

    private static VoiceInputCueClip Clip(int milliseconds, float left, float right) => new(
        Enumerable.Range(0, milliseconds * 96).Select(i => i % 2 == 0 ? left : right).ToArray());

    private static float[] Read(ISampleProvider source, int milliseconds)
    {
        var buffer = new float[milliseconds * 96];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = source.Read(buffer, total, Math.Min(514, buffer.Length - total));
            if (read == 0) break;
            total += read;
        }
        return buffer[..total];
    }

    private sealed class Playback(ISampleProvider source) : IVoiceInputCuePlayback
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISampleProvider Source { get; } = source;
        public bool Disposed { get; private set; }
        public Task Completion => _completion.Task;
        public void Play() { }
        public void Dispose() { Disposed = true; _completion.TrySetResult(); }
    }

    private sealed class AudioFiles : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "custom-cues-" + Guid.NewGuid().ToString("N"));
        public AudioFiles() => Directory.CreateDirectory(DirectoryPath);
        public string Write(string name, int sampleRate, int channels, double seconds, float value)
        {
            var path = Path.Combine(DirectoryPath, name);
            using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
            var samples = Enumerable.Repeat(value, (int)(sampleRate * seconds) * channels).ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
            return path;
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
