using System.IO;
using System.Text.Json;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Output;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class VoiceInputCueTests
{
    [Fact]
    public void LegacyConfigurationDoesNotSelectAnOutputOrEnableCues()
    {
        var configuration = JsonSerializer.Deserialize<AppConfiguration>("{}");
        Assert.NotNull(configuration);
        ConfigurationStore.NormalizePrompts(configuration);
        Assert.False(configuration.VrChatVoiceInput.Cues.Enabled);
        Assert.False(configuration.VrChatVoiceInput.Cues.EchoEnabled);
        Assert.True(configuration.VrChatVoiceInput.Cues.StartEnabled);
        Assert.True(configuration.VrChatVoiceInput.Cues.OngoingEnabled);
        Assert.True(configuration.VrChatVoiceInput.Cues.EndEnabled);
        Assert.Null(configuration.VrChatVoiceInput.Cues.StartFilePath);
        Assert.Null(configuration.VrChatVoiceInput.Cues.OngoingFilePath);
        Assert.Null(configuration.VrChatVoiceInput.Cues.EndFilePath);
    }

    [Fact]
    public void CueConfigurationRoundTripsAndClonesIndependently()
    {
        var original = new AppConfiguration();
        original.VrChatVoiceInput.Cues = new VoiceInputCueConfiguration
        {
            Enabled = true, EchoEnabled = true, VolumePercent = 71,
            StartEnabled = false, OngoingEnabled = true, EndEnabled = false,
            StartFilePath = "sounds/start.wav", OngoingFilePath = "sounds/loop.wav", EndFilePath = "sounds/end.wav"
        };
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(original, options);
        var copy = JsonSerializer.Deserialize<AppConfiguration>(json, options)!.VrChatVoiceInput.Cues;
        Assert.Equal(71, copy.VolumePercent);
        Assert.True(copy.Enabled);
        Assert.True(copy.EchoEnabled);
        Assert.False(copy.StartEnabled);
        Assert.True(copy.OngoingEnabled);
        Assert.False(copy.EndEnabled);
        Assert.Equal("sounds/start.wav", copy.StartFilePath);
        Assert.Equal("sounds/loop.wav", copy.OngoingFilePath);
        Assert.Equal("sounds/end.wav", copy.EndFilePath);
        var clone = copy.Clone();
        clone.VolumePercent = 25;
        Assert.Equal(71, copy.VolumePercent);

        var runtimeClone = (AppConfiguration)typeof(MainWindow)
            .GetMethod("CloneConfiguration", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [original])!;
        Assert.NotSame(original.VrChatVoiceInput.Cues, runtimeClone.VrChatVoiceInput.Cues);
        Assert.Equal(copy.VolumePercent, runtimeClone.VrChatVoiceInput.Cues.VolumePercent);
        Assert.True(runtimeClone.VrChatVoiceInput.Cues.EchoEnabled);
        Assert.Equal(copy.StartFilePath, runtimeClone.VrChatVoiceInput.Cues.StartFilePath);
        Assert.Equal(copy.OngoingFilePath, runtimeClone.VrChatVoiceInput.Cues.OngoingFilePath);
        Assert.Equal(copy.EndFilePath, runtimeClone.VrChatVoiceInput.Cues.EndFilePath);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(150, 100)]
    public void InvalidVolumeIsClampedAndNullCuesAreRestored(int volume, int expected)
    {
        var configuration = new AppConfiguration();
        configuration.VrChatVoiceInput.Cues = null!;
        ConfigurationStore.NormalizePrompts(configuration);
        configuration.VrChatVoiceInput.Cues.VolumePercent = volume;
        ConfigurationStore.NormalizePrompts(configuration);
        Assert.Equal(expected, configuration.VrChatVoiceInput.Cues.VolumePercent);
    }

    [Fact]
    public void RecordingContainsContinuousNonRepeatingQuietRadioNoise()
    {
        var source = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration { VolumePercent = 100 });
        var opening = ReadMilliseconds(source, VoiceInputCueSampleProvider.OpeningMilliseconds);
        var noise = ReadMilliseconds(source, 2500);
        Assert.True(Rms(opening) > Rms(noise));
        for (var offset = 0; offset < noise.Length; offset += 4800)
        {
            Assert.InRange(Rms(noise.Skip(offset).Take(4800)), 0.005, 0.15);
        }
        Assert.False(noise.Take(4800).SequenceEqual(noise.Skip(4800).Take(4800)));
        Assert.InRange(noise.Average(sample => (double)sample), -0.002, 0.002);
        Assert.All(opening.Concat(noise), sample => Assert.InRange(sample, -1f, 1f));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EachStageCanBeEnabledIndependently(bool start, bool ongoing, bool end)
    {
        var source = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration
        {
            StartEnabled = start, OngoingEnabled = ongoing, EndEnabled = end
        });
        var opening = ReadMilliseconds(source, VoiceInputCueSampleProvider.OpeningMilliseconds);
        Assert.Equal(start || ongoing, Rms(opening) > 0);
        var middle = ReadMilliseconds(source, 300);
        Assert.Equal(ongoing, Rms(middle) > 0);
        source.End();
        var ending = ReadToEnd(source);
        // The first 10 ms fade the last sample; the closing cue begins after the fade.
        Assert.Equal(end, Rms(ending.Skip(960)) > 0);
        Assert.Equal((10 + (end ? VoiceInputCueSampleProvider.ClosingMilliseconds : 0)) * 96, ending.Length);
    }

    [Fact]
    public void ImmediateReleaseInterruptsOpeningAndRepeatedEndDoesNotRestartClosing()
    {
        var source = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration());
        ReadMilliseconds(source, 20);
        source.End();
        var first = ReadMilliseconds(source, 50);
        source.End();
        var rest = ReadToEnd(source);
        Assert.Equal(230 * 96, first.Length + rest.Length);
        Assert.True(Rms(rest) > 0);
        Assert.Equal(0, rest[^1]);
        Assert.Equal(0, source.Read(new float[512], 0, 512));
    }

    [Fact]
    public void GainStereoAndChunkBoundariesAreStable()
    {
        var full = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration { VolumePercent = 100 });
        var half = new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration { VolumePercent = 50 });
        var expected = ReadMilliseconds(full, 400);
        var actual = new List<float>();
        while (actual.Count < expected.Length)
        {
            var count = Math.Min(514, expected.Length - actual.Count);
            var buffer = Enumerable.Repeat(99f, count + 4).ToArray();
            Assert.Equal(count, half.Read(buffer, 2, count));
            Assert.Equal(99f, buffer[0]);
            Assert.Equal(99f, buffer[^1]);
            actual.AddRange(buffer.Skip(2).Take(count));
        }
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i] * 0.5f, actual[i], 6);
            if (i % 2 == 0)
            {
                Assert.Equal(actual[i], actual[i + 1]);
            }
        }
    }

    [Theory]
    [InlineData(false, true, 35)]
    [InlineData(true, false, 35)]
    [InlineData(true, true, 0)]
    public void DisabledUninstalledOrMutedCuesNeverOpenAnOutput(bool enabled, bool installed, int volume)
    {
        using var fixture = new OutputFixture();
        fixture.Status = installed ? OutputFixture.Ready() : new VbCableStatus(false, null, null);
        using var output = fixture.Create(new VoiceInputCueConfiguration
        {
            Enabled = enabled, VolumePercent = volume
        });
        Assert.False(output.Start());
        Assert.Empty(fixture.Playbacks);
    }

    [Fact]
    public async Task OutputUsesDetectedCableAndFinishesClosingBeforeDisposal()
    {
        using var fixture = new OutputFixture();
        using var output = fixture.Create();
        Assert.True(output.Start());
        var playback = Assert.Single(fixture.Playbacks);
        Assert.Equal("cable-playback-endpoint", playback.DeviceId);
        ReadMilliseconds(playback.Source, 500);
        var ending = output.EndAsync();
        Assert.False(ending.IsCompleted);
        Assert.Equal(0, playback.DisposeCount);
        Assert.True(Rms(ReadToEnd(playback.Source)) > 0);
        playback.Complete();
        await ending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, playback.DisposeCount);
    }

    [Fact]
    public async Task RapidRestartStopsOldPlaybackAndLateCompletionCannotStopNewPlayback()
    {
        using var fixture = new OutputFixture();
        using var output = fixture.Create();
        output.Start();
        var oldPlayback = Assert.Single(fixture.Playbacks);
        var oldEnding = output.EndAsync();
        output.Start();
        await oldEnding.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, oldPlayback.DisposeCount);
        var newPlayback = fixture.Playbacks[1];
        oldPlayback.Complete();
        ReadMilliseconds(newPlayback.Source, 1000);
        Assert.Equal(0, newPlayback.DisposeCount);
        var newEnding = output.EndAsync();
        ReadToEnd(newPlayback.Source);
        newPlayback.Complete();
        await newEnding.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, newPlayback.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndDisposeAreIdempotentAndStopOngoingAudio(bool echoEnabled)
    {
        using var fixture = new OutputFixture();
        var output = fixture.Create(new VoiceInputCueConfiguration { Enabled = true, EchoEnabled = echoEnabled });
        output.Start();
        Assert.Equal(echoEnabled ? 2 : 1, fixture.Playbacks.Count);
        var ending = output.EndAsync();
        output.Cancel();
        output.Cancel();
        output.Dispose();
        Assert.False(output.Start());
        await ending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(fixture.Playbacks, playback => Assert.Equal(1, playback.DisposeCount));
    }

    [Fact]
    public async Task DeviceFailuresAreContainedAndNextRecordingCanRetry()
    {
        using var fixture = new OutputFixture();
        var attempts = 0;
        var playback = new FakePlayback("cable-playback-endpoint", new VoiceInputCueSampleProvider(new VoiceInputCueConfiguration()));
        using var output = new VoiceInputCueOutput(OutputFixture.Configuration(), fixture.Log, (_, _) =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("Disconnected endpoint");
            }
            return playback;
        }, OutputFixture.Ready);
        Assert.False(output.Start());
        Assert.True(output.Start());
        var ending = output.EndAsync();
        playback.Fail();
        await ending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, playback.DisposeCount);
    }

    private static float[] ReadMilliseconds(ISampleProvider source, int milliseconds)
    {
        var buffer = new float[milliseconds * 96];
        Assert.Equal(buffer.Length, source.Read(buffer, 0, buffer.Length));
        return buffer;
    }

    [Fact]
    public async Task LocalPreviewUsesRequestedOutputAndCancellationDisposesIt()
    {
        using var cancellation = new CancellationTokenSource();
        FakePlayback? playback = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = VoiceInputCuePreview.PlayAsync(new VoiceInputCueConfiguration { EchoEnabled = true }, "headphones", cancellation.Token,
            (id, source) => playback = new FakePlayback(id, source, () => started.TrySetResult()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(playback);
        Assert.Equal("headphones", playback.DeviceId);
        Assert.True(Rms(ReadMilliseconds(playback.Source, 50)) > 0);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview);
        Assert.Equal(1, playback.DisposeCount);
    }

    [Fact]
    public async Task PreviewPropagatesDeviceFailureInsteadOfSilentlyFinishing()
    {
        FakePlayback? playback = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = VoiceInputCuePreview.PlayAsync(new VoiceInputCueConfiguration(), "headphones", CancellationToken.None,
            (id, source) => playback = new FakePlayback(id, source, () => started.TrySetResult()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(playback);
        playback.Fail();
        await Assert.ThrowsAsync<IOException>(() => preview);
        Assert.Equal(1, playback.DisposeCount);
    }

    [Fact]
    public async Task PreviewRejectsUnexpectedEarlyStop()
    {
        FakePlayback? playback = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = VoiceInputCuePreview.PlayAsync(new VoiceInputCueConfiguration(), "headphones", CancellationToken.None,
            (id, source) => playback = new FakePlayback(id, source, () => started.TrySetResult()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(playback);
        playback.Complete();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview);
        Assert.Equal(1, playback.DisposeCount);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, false)]
    public async Task EmptyOrMutedPreviewDoesNotOpenAnOutput(int volume, bool stages)
    {
        var opened = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => VoiceInputCuePreview.PlayAsync(new VoiceInputCueConfiguration
        {
            VolumePercent = volume, StartEnabled = stages, OngoingEnabled = stages, EndEnabled = stages
        }, "headphones", CancellationToken.None, (id, source) =>
        {
            opened = true;
            return new FakePlayback(id, source);
        }));
        Assert.False(opened);
    }

    [Fact]
    public async Task EchoDuplicatesAudioAndBothOutputsFinishTheirClosingCues()
    {
        using var fixture = new OutputFixture();
        using var output = fixture.Create(new VoiceInputCueConfiguration { Enabled = true, EchoEnabled = true });
        Assert.True(output.Start());
        Assert.Equal(2, fixture.Playbacks.Count);
        var game = fixture.Playbacks[0];
        var echo = fixture.Playbacks[1];
        Assert.Equal("headphones", echo.DeviceId);
        Assert.NotSame(game.Source, echo.Source);
        Assert.Equal(ReadMilliseconds(game.Source, 500), ReadMilliseconds(echo.Source, 500));
        var ended = output.EndAsync();
        Assert.Equal(ReadToEnd(game.Source), ReadToEnd(echo.Source));
        game.Complete();
        Assert.False(ended.IsCompleted);
        echo.Complete();
        await ended.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, game.DisposeCount);
        Assert.Equal(1, echo.DisposeCount);
    }

    [Theory]
    [InlineData("cable-playback-endpoint")]
    [InlineData("CABLE-SIXTEEN-CHANNEL-ENDPOINT")]
    [InlineData(null)]
    public void MissingOrCableDefaultOutputDoesNotDuplicateGameAudio(string? defaultDevice)
    {
        using var fixture = new OutputFixture();
        fixture.Status = fixture.Status with { DeviceIds = ["cable-playback-endpoint", "cable-sixteen-channel-endpoint"] };
        fixture.EchoDeviceId = defaultDevice;
        using var output = fixture.Create(new VoiceInputCueConfiguration { Enabled = true, EchoEnabled = true });
        Assert.True(output.Start());
        Assert.Single(fixture.Playbacks);
    }

    [Fact]
    public void DisabledEchoDoesNotLookUpLocalOutput()
    {
        using var fixture = new OutputFixture();
        using var output = new VoiceInputCueOutput(OutputFixture.Configuration(), fixture.Log,
            fixture.CreatePlayback, OutputFixture.Ready, () => throw new InvalidOperationException("Must not resolve default output"));
        Assert.True(output.Start());
        Assert.Single(fixture.Playbacks);
    }

    [Fact]
    public async Task EchoFailureDoesNotStopGamePlaybackButGameFailureStopsEcho()
    {
        using var fixture = new OutputFixture();
        using var output = fixture.Create(new VoiceInputCueConfiguration { Enabled = true, EchoEnabled = true });
        Assert.True(output.Start());
        var game = fixture.Playbacks[0];
        var echo = fixture.Playbacks[1];
        echo.Fail();
        await echo.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, game.DisposeCount);
        Assert.True(Rms(ReadMilliseconds(game.Source, 500)) > 0);
        var ending = output.EndAsync();
        game.Complete();
        await ending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(output.Start());
        var nextGame = fixture.Playbacks[2];
        var nextEcho = fixture.Playbacks[3];
        nextGame.Fail();
        await nextEcho.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, nextGame.DisposeCount);
    }

    [Fact]
    public void EchoInitializationFailureDoesNotPreventGamePlayback()
    {
        using var fixture = new OutputFixture();
        using var output = new VoiceInputCueOutput(new VoiceInputCueConfiguration { Enabled = true, EchoEnabled = true }, fixture.Log,
            (id, source) => id == "headphones" ? throw new IOException("Headphones disconnected") : fixture.CreatePlayback(id, source),
            OutputFixture.Ready, () => "headphones");
        Assert.True(output.Start());
        Assert.Equal(0, Assert.Single(fixture.Playbacks).DisposeCount);
    }

    [Fact]
    public void RestartStopsBothOldOutputsAndUsesCurrentDefaultHeadphones()
    {
        using var fixture = new OutputFixture();
        using var output = fixture.Create(new VoiceInputCueConfiguration { Enabled = true, EchoEnabled = true });
        Assert.True(output.Start());
        var old = fixture.Playbacks.ToArray();
        fixture.EchoDeviceId = "new-headphones";
        Assert.True(output.Start());
        Assert.All(old, playback => Assert.Equal(1, playback.DisposeCount));
        foreach (var playback in old) playback.Complete();
        Assert.Equal("new-headphones", fixture.Playbacks[3].DeviceId);
        Assert.All(fixture.Playbacks.Skip(2), playback => Assert.Equal(0, playback.DisposeCount));
    }

    [Fact]
    public async Task GameMicrophonePreviewAlsoEchoesAndCancelsBothOutputs()
    {
        using var fixture = new OutputFixture();
        using var cancellation = new CancellationTokenSource();
        var playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var preview = VoiceInputCuePreview.PlayAsync(new VoiceInputCueConfiguration { EchoEnabled = true },
            "game", cancellation.Token, (id, source) =>
            {
                var playback = new FakePlayback(id, source, () => { if (Interlocked.Increment(ref startedCount) == 2) playing.TrySetResult(); });
                fixture.Playbacks.Add(playback);
                return playback;
            }, new VoiceInputCueEchoOptions(fixture.Log, ["game"], () => "headphones"));
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "game", "headphones" }, fixture.Playbacks.Select(playback => playback.DeviceId));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview);
        Assert.All(fixture.Playbacks, playback => Assert.Equal(1, playback.DisposeCount));
    }

    private static float[] ReadToEnd(ISampleProvider source)
    {
        var samples = new List<float>();
        var buffer = new float[512];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            samples.AddRange(buffer.Take(read));
            Assert.True(samples.Count < 48000, "The ending must finish within half a second.");
        }
        return samples.ToArray();
    }

    private static double Rms(IEnumerable<float> samples)
    {
        var data = samples.ToArray();
        return data.Length == 0 ? 0 : Math.Sqrt(data.Average(sample => (double)sample * sample));
    }

    private sealed class OutputFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "voice-cue-tests-" + Guid.NewGuid().ToString("N"));
        public OutputFixture() => Log = new AppLog(_directory);
        public AppLog Log { get; }
        public List<FakePlayback> Playbacks { get; } = [];
        public VbCableStatus Status { get; set; } = Ready();
        public string? EchoDeviceId { get; set; } = "headphones";
        public static VbCableStatus Ready() => new(true, "cable-playback-endpoint", "cable-capture-endpoint");
        public static VoiceInputCueConfiguration Configuration() => new()
        {
            Enabled = true
        };
        public VoiceInputCueOutput Create(VoiceInputCueConfiguration? configuration = null) =>
            new(configuration ?? Configuration(), Log, CreatePlayback, () => Status, () => EchoDeviceId);
        public FakePlayback CreatePlayback(string deviceId, ISampleProvider source)
        {
            var playback = new FakePlayback(deviceId, source);
            Playbacks.Add(playback);
            return playback;
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakePlayback(string deviceId, ISampleProvider source, Action? onPlay = null) : IVoiceInputCuePlayback
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DeviceId { get; } = deviceId;
        public ISampleProvider Source { get; } = source;
        public int DisposeCount { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => _completion.Task;
        public void Play() => onPlay?.Invoke();
        public void Complete() => _completion.TrySetResult();
        public void Fail() => _completion.TrySetException(new IOException("Endpoint disconnected during playback"));
        public void Dispose()
        {
            DisposeCount++;
            Complete();
            Disposed.TrySetResult();
        }
    }
}
