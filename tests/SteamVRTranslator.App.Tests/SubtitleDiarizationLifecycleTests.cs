using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SubtitleDiarizationLifecycleTests
{
    [Fact]
    public async Task ConsecutiveSegmentsAndThresholdChangesReuseBothModels()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Capture!.Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        fixture.Configuration.Subtitles.Diarization.ClusteringThreshold = .75;
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        fixture.Capture.Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 2);
        Assert.Single(fixture.Models);
        Assert.Equal(2, fixture.Models[0].EmbeddingCalls);
        Assert.Equal([.9, .75], fixture.Models[0].Thresholds.ToArray());
        Assert.Equal(1, fixture.AsrCreations);
    }

    [Fact]
    public async Task PausingAndRestartingKeepsModelsButStoppingAllUnloadsThem()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        await fixture.Controller.StopListeningAsync();
        Assert.False(fixture.Models[0].Disposed);
        await fixture.Start();
        Assert.Single(fixture.Models);
        await fixture.Controller.StopAllAsync();
        Assert.True(fixture.Models[0].Disposed);
        Assert.Equal(1, fixture.Models[0].DisposeCount);
        await fixture.Start();
        Assert.Equal(2, fixture.Models.Count);
    }

    [Theory]
    [InlineData("threads")]
    [InlineData("model")]
    public async Task ModelSettingsWaitForActiveInferenceAndKeepAsrAlive(string setting)
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var finish = Signal();
        fixture.CreateModels = (_, _) => fixture.Models.Count == 0 ? new Models
        {
            OnSegment = () => { started.TrySetResult(); finish.Task.GetAwaiter().GetResult(); }
        } : new Models();
        await fixture.Start();
        fixture.Capture!.Emit();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var configuration = fixture.Configuration.Subtitles.Diarization;
            if (setting == "threads" && Environment.ProcessorCount > 1) configuration.CpuThreadCount = 2;
            else configuration.EmbeddingModelPath = "replacement.onnx";
            fixture.Controller.ApplyConfiguration(fixture.Configuration);
            Assert.False(fixture.Models[0].Disposed);
            finish.SetResult();
            await Eventually(() => fixture.Controller.History.Entries.Count == 1);
            fixture.Capture.Emit();
            await Eventually(() => fixture.Controller.History.Entries.Count == 2);
            Assert.Equal(2, fixture.Models.Count);
            Assert.True(fixture.Models[0].Disposed);
            Assert.Equal(0, fixture.Models[0].ActiveAtDispose);
            Assert.Equal(1, fixture.AsrCreations);
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task LiveAndReplayShareModelsButKeepSpeakerIdentitiesSeparate()
    {
        await using var fixture = new Fixture();
        var configuration = fixture.Configuration.Subtitles.Diarization;
        await using var first = await fixture.Processor.AcquireModelsAsync(configuration, CancellationToken.None);
        await using var second = await fixture.Processor.AcquireModelsAsync(configuration, CancellationToken.None);
        var liveSpeakers = new SpeakerIdentityRegistry();
        liveSpeakers.ReserveAnonymous();
        var replaySpeakers = new SpeakerIdentityRegistry();
        var results = await Task.WhenAll(
            fixture.Processor.AnalyzeAsync(fixture.AudioPath, configuration, liveSpeakers, first, CancellationToken.None),
            fixture.Processor.AnalyzeAsync(fixture.AudioPath, configuration, replaySpeakers, second, CancellationToken.None));
        Assert.Single(fixture.Models);
        Assert.Equal(1, fixture.Models[0].MaximumConcurrentCalls);
        Assert.Equal(1, Assert.Single(results[0].Segments).Speaker);
        Assert.Equal(0, Assert.Single(results[1].Segments).Speaker);
        await first.DisposeAsync();
        Assert.False(fixture.Models[0].Disposed);
    }

    [Fact]
    public async Task CancellationWaitsForNativeWorkWithoutDisposingOrPublishingItsResult()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var finish = Signal();
        fixture.CreateModels = (_, _) => new Models
        {
            OnSegment = () => { started.TrySetResult(); finish.Task.GetAwaiter().GetResult(); }
        };
        await fixture.Start();
        fixture.Capture!.Emit();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stopping = fixture.Controller.StopAllAsync();
            Assert.False(stopping.IsCompleted);
            Assert.False(fixture.Models[0].Disposed);
            finish.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(fixture.Controller.History.Entries);
            Assert.Equal(0, fixture.Models[0].EmbeddingCalls);
            Assert.True(fixture.Models[0].Disposed);
            Assert.Equal(0, fixture.Models[0].ActiveAtDispose);
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task CancelledWarmupCanRestartWithoutLeavingAnAsrClient()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        fixture.CreateModels = (_, token) =>
        {
            started.TrySetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new Models();
        };
        await fixture.Controller.StartListeningAsync(() => 123);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Controller.StopListeningAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, fixture.AsrCreations);
        fixture.CreateModels = (_, _) => new Models();
        await fixture.Start();
        Assert.Single(fixture.Models);
    }

    [Fact]
    public async Task LoadingFailureAndInferenceFailureBothAllowRetry()
    {
        await using var fixture = new Fixture();
        fixture.CreateModels = (_, _) => throw new IOException("model unavailable");
        await fixture.Controller.StartListeningAsync(() => 123);
        await Eventually(() => !fixture.Controller.IsListening && fixture.Controller.ListeningState == SubtitleListeningState.Error);
        fixture.CreateModels = (_, _) => new Models { OnSegment = () => throw new IOException("inference failed") };
        await fixture.Start();
        fixture.Capture!.Emit();
        await Eventually(() => !fixture.Controller.IsListening && fixture.Controller.ListeningState == SubtitleListeningState.Error);
        Assert.True(fixture.Models[0].Disposed);
        fixture.CreateModels = (_, _) => new Models();
        await fixture.Start();
        fixture.Capture!.Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        Assert.Equal(2, fixture.Models.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledDiarizationOrRemoteAsrDoesNotLoadLocalSpeakerModels(bool remote)
    {
        await using var fixture = new Fixture();
        if (remote) fixture.Configuration.Subtitles.AsrBackend = SubtitleAsrBackends.VibeVoiceApi;
        else fixture.Configuration.Subtitles.Diarization.Enabled = false;
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        await fixture.Start();
        Assert.Empty(fixture.Models);
        Assert.Equal(remote ? 0 : 1, fixture.AsrCreations);
    }

    [Fact]
    public async Task DisablingDiarizationReleasesItAtTheNextSegmentWithoutReloadingAsr()
    {
        await using var fixture = new Fixture();
        await fixture.Start();
        fixture.Configuration.Subtitles.Diarization.Enabled = false;
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        fixture.Capture!.Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        Assert.True(fixture.Models[0].Disposed);
        Assert.Equal(1, fixture.AsrCreations);
        Assert.Equal(0, fixture.Models[0].EmbeddingCalls);
    }

    [Fact]
    public async Task RealModelsProcessRepeatedSpeechWithoutReloadingWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("SVT_RUN_DIARIZATION_TEST") != "1") return;
        var root = Environment.GetEnvironmentVariable("SVT_DIARIZATION_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "svt-diarization-native-test");
        var audioPath = Environment.GetEnvironmentVariable("SVT_DIARIZATION_TEST_AUDIO");
        Assert.True(File.Exists(audioPath), "Set SVT_DIARIZATION_TEST_AUDIO to a speech recording.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using (var download = new SubtitleDiarizationDownloadService(root))
            await download.DownloadAsync(useMirror: false, cancellation.Token);
        var configuration = new SubtitleDiarizationConfiguration
        {
            SegmentationModelPath = Path.Combine(root, SubtitleDiarizationAssetCatalog.Assets[0].TargetPath),
            EmbeddingModelPath = Path.Combine(root, SubtitleDiarizationAssetCatalog.Assets[1].TargetPath)
        };
        var creations = 0;
        await using var processor = new SubtitleAudioProcessor(new AppLog(root), (key, token) =>
        {
            Interlocked.Increment(ref creations);
            return new ResidentSubtitleDiarizationModels(key, token);
        });
        processor.UpdateConfiguration(configuration);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var registry = new SpeakerIdentityRegistry();
            configuration.ClusteringThreshold = attempt == 0 ? .9 : .8;
            var document = await processor.AnalyzeAsync(audioPath!, configuration, registry, cancellation.Token);
            Assert.NotEmpty(document.Segments);
            Assert.True(registry.Count > 0);
        }
        Assert.Equal(1, creations);
        await processor.ReleaseIdleModelsAsync();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "subtitle-resident-" + Guid.NewGuid().ToString("N"));
        public AppConfiguration Configuration { get; } = new();
        public SubtitleAudioProcessor Processor { get; }
        public SubtitleSessionController Controller { get; }
        public string AudioPath { get; }
        public Capture? Capture { get; private set; }
        private readonly ConcurrentQueue<Models> _models = new();
        public IReadOnlyList<Models> Models => _models.ToArray();
        public Func<SubtitleDiarizationModelKey, CancellationToken, Models> CreateModels { get; set; } = (_, _) => new();
        public int AsrCreations;
        private readonly HttpClient _http = new();
        public Fixture()
        {
            var log = new AppLog(DirectoryPath);
            Configuration.Subtitles.TranslateText = false;
            Processor = new SubtitleAudioProcessor(log, (key, token) =>
            {
                var models = CreateModels(key, token); _models.Enqueue(models); return models;
            });
            Controller = new SubtitleSessionController(Configuration, log, _http, _ => Capture = new Capture(),
                (_, _) => { Interlocked.Increment(ref AsrCreations); return new Asr(); }, () => true, TimeSpan.FromMilliseconds(10), Processor);
            AudioPath = Path.Combine(DirectoryPath, "audio.wav");
            using var writer = new WaveFileWriter(AudioPath, new WaveFormat(16000, 16, 1));
            writer.Write(new byte[32000], 0, 32000);
        }
        public async Task Start()
        {
            await Controller.StartListeningAsync(() => 123);
            await Eventually(() => Controller.ListeningState == SubtitleListeningState.Listening);
        }
        public async ValueTask DisposeAsync() { await Controller.DisposeAsync(); _http.Dispose(); Directory.Delete(DirectoryPath, true); }
    }

    private sealed class Models : ISubtitleDiarizationModels
    {
        private int _active;
        public Action? OnSegment { get; init; }
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public int EmbeddingCalls { get; private set; }
        public int ActiveAtDispose { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }
        public ConcurrentQueue<double> Thresholds { get; } = new();
        public IReadOnlyList<SubtitleSpeakerSegment> Segment(float[] samples, double threshold)
        {
            Assert.False(Disposed);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, Interlocked.Increment(ref _active));
            Thresholds.Enqueue(threshold);
            try { OnSegment?.Invoke(); return [new(0, 0, (double)samples.Length / SubtitleAudioProcessor.SampleRate)]; }
            finally { Interlocked.Decrement(ref _active); }
        }
        public float[]? Embed(float[] samples) { Assert.False(Disposed); EmbeddingCalls++; return [1, 0]; }
        public void Dispose() { Disposed = true; DisposeCount++; ActiveAtDispose = Volatile.Read(ref _active); }
    }

    private sealed class Capture : ISubtitleAudioCapture
    {
        private readonly TaskCompletionSource _completion = Signal();
        public event EventHandler<ProcessLoopbackAudioSegment>? SegmentReady;
        public Task Completion => _completion.Task;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public void Emit() => SegmentReady?.Invoke(this, new(new byte[32000], new WaveFormat(16000, 16, 1), TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        public ValueTask DisposeAsync() { _completion.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class Asr : ISubtitleLocalTranscriber
    {
        public Task<CommandTranscriptionResult> TranscribeAsync(CommandAudioInput audio, string diagnosticTag, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandTranscriptionResult("recognized speech", TimeSpan.Zero));
        public void Dispose() { }
    }
}
