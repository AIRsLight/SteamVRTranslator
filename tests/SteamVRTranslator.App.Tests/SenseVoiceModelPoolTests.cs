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

public sealed class SenseVoiceModelPoolTests
{
    [Fact]
    public async Task ConcurrentClientsLoadOnceAndOnlyTheLastReleaseUnloads()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var ready = Signal();
        fixture.Create = token => { started.TrySetResult(); ready.Task.Wait(token); return new Worker(); };
        var first = fixture.Acquire();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = fixture.Acquire();
        ready.SetResult();
        var leases = await Task.WhenAll(first, second);
        Assert.Single(fixture.Workers);
        await leases[0].DisposeAsync();
        Assert.False(fixture.Workers[0].Disposed);
        await leases[1].DisposeAsync();
        Assert.True(fixture.Workers[0].Disposed);
        Assert.Equal(1, fixture.Workers[0].DisposeCount);
    }

    [Fact]
    public async Task CancellingOneWarmupWaiterKeepsTheOtherWaiterLoading()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        var ready = Signal();
        CancellationToken modelToken = default;
        fixture.Create = token => { modelToken = token; started.TrySetResult(); ready.Task.Wait(token); return new Worker(); };
        using var cancelled = new CancellationTokenSource();
        var first = fixture.Acquire(cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = fixture.Acquire();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(modelToken.IsCancellationRequested);
        ready.SetResult();
        await using var lease = await second;
        Assert.Single(fixture.Workers);
    }

    [Fact]
    public async Task CancelledLastWarmupWaiterReleasesStartupAndCanRetry()
    {
        await using var fixture = new Fixture();
        var started = Signal();
        fixture.Create = token =>
        {
            started.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return new Worker();
        };
        using var cancellation = new CancellationTokenSource();
        var loading = fixture.Acquire(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(TimeSpan.FromSeconds(3)));
        fixture.Create = _ => new Worker();
        await using var lease = await fixture.Acquire();
        Assert.Single(fixture.Workers);
    }

    [Fact]
    public async Task ReacquisitionWaitsForTheOldProcessToFinishDisposing()
    {
        await using var fixture = new Fixture();
        var disposing = Signal();
        var finish = Signal();
        fixture.Create = _ => fixture.Workers.Count == 0 ? new Worker
        {
            OnDispose = () => { disposing.TrySetResult(); finish.Task.GetAwaiter().GetResult(); }
        } : new Worker();
        var first = await fixture.Acquire();
        var stopping = first.DisposeAsync().AsTask();
        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var next = fixture.Acquire();
            Assert.False(next.IsCompleted);
            Assert.Single(fixture.Workers);
            finish.SetResult();
            await using var second = await next.WaitAsync(TimeSpan.FromSeconds(3));
            await stopping;
            Assert.Equal(2, fixture.Workers.Count);
        }
        finally { finish.TrySetResult(); }
    }

    [Fact]
    public async Task WarmupFailureCanBeRetriedWithoutCachingTheFailedInstance()
    {
        await using var fixture = new Fixture();
        fixture.Create = _ => throw new IOException("model unavailable");
        await Assert.ThrowsAsync<IOException>(() => fixture.Acquire());
        fixture.Create = _ => new Worker();
        await using var lease = await fixture.Acquire();
        Assert.Single(fixture.Workers);
    }

    [Fact]
    public async Task EquivalentPathsAndUnrelatedSettingsReuseTheModel()
    {
        await using var fixture = new Fixture();
        await using var first = await fixture.Acquire();
        fixture.Configuration.SenseVoiceModelPath = Path.GetFullPath(fixture.Configuration.SenseVoiceModelPath, AppContext.BaseDirectory).ToLowerInvariant();
        fixture.Configuration.HoldThresholdMilliseconds++;
        fixture.Configuration.SenseVoiceVulkanDeviceName = "Changed label";
        fixture.Configuration.SenseVoiceVulkanDeviceIndex = 3; // Ignored by CPU.
        await using var second = await fixture.Acquire();
        Assert.Single(fixture.Workers);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("vad")]
    [InlineData("language")]
    [InlineData("backend")]
    [InlineData("device")]
    [InlineData("executable")]
    public async Task DifferentNativeSettingsUseSeparateModels(string setting)
    {
        await using var fixture = new Fixture();
        fixture.Configuration.SenseVoiceBackend = "vulkan";
        await using var first = await fixture.Acquire();
        switch (setting)
        {
            case "model": fixture.Configuration.SenseVoiceModelPath = "other.gguf"; break;
            case "vad": fixture.Configuration.SenseVoiceVadModelPath = null; break;
            case "language": fixture.Configuration.EffectiveRecognitionLanguage = "ja"; break;
            case "backend": fixture.Configuration.SenseVoiceBackend = "cpu"; break;
            case "device": fixture.Configuration.SenseVoiceVulkanDeviceIndex = 2; break;
            case "executable": fixture.Configuration.SenseVoiceVulkanExecutablePath = "other.exe"; break;
        }
        await using var second = await fixture.Acquire();
        Assert.Equal(2, fixture.Workers.Count);
    }

    [Fact]
    public async Task VoiceInputRunsBeforeQueuedSubtitlesWithoutInterruptingTheCurrentRequest()
    {
        await using var fixture = new Fixture();
        var finish = Signal();
        fixture.Create = _ => new Worker { Respond = async (path, token) =>
        {
            var text = await File.ReadAllTextAsync(path, token);
            if (text == "active") await finish.Task.WaitAsync(token);
            return text;
        }};
        await using var lease = await fixture.Acquire();
        using var active = fixture.Audio("active");
        using var subtitle = fixture.Audio("subtitle");
        using var voice = fixture.Audio("voice");
        var current = Request(lease, active);
        await Eventually(() => fixture.Workers[0].Inputs.Count == 1);
        var queuedSubtitle = Request(lease, subtitle);
        await Eventually(() => lease.QueuedRequests == 1);
        var queuedVoice = Request(lease, voice, SenseVoiceRequestPriority.Interactive);
        await Eventually(() => lease.QueuedRequests == 2);
        finish.SetResult();
        await Task.WhenAll(current, queuedSubtitle, queuedVoice).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["active", "voice", "subtitle"], fixture.Workers[0].Inputs.ToArray());
        Assert.Equal(1, fixture.Workers[0].MaximumConcurrentRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrReleaseDuringRecognitionKeepsTheModelAndItsAudioAlive(bool releaseLease)
    {
        await using var fixture = new Fixture();
        var finish = Signal();
        fixture.Create = _ => new Worker { Respond = async (path, token) =>
        {
            var text = await File.ReadAllTextAsync(path, token);
            if (text == "cancelled subtitle")
            {
                await finish.Task.WaitAsync(token);
                Assert.Equal(text, await File.ReadAllTextAsync(path, token));
            }
            return text;
        }};
        await using var subtitle = await fixture.Acquire();
        await using var voice = await fixture.Acquire();
        using var cancellation = new CancellationTokenSource();
        using var firstAudio = fixture.Audio("cancelled subtitle");
        var first = Request(subtitle, firstAudio, token: cancellation.Token);
        await Eventually(() => fixture.Workers[0].Inputs.Count == 1);
        if (releaseLease) await subtitle.DisposeAsync(); else cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        firstAudio.Dispose();
        Assert.False(fixture.Workers[0].Disposed);
        Assert.False(fixture.Workers[0].LastToken.IsCancellationRequested);
        using var nextAudio = fixture.Audio("voice result");
        var next = Request(voice, nextAudio, SenseVoiceRequestPriority.Interactive);
        finish.SetResult();
        Assert.Equal("voice result", (await next.WaitAsync(TimeSpan.FromSeconds(3))).Text);
        Assert.Single(fixture.Workers);
        Assert.False(File.Exists(fixture.Workers[0].Paths.First()));
        Assert.Contains(fixture.Logs, line => line.Contains("stage=model-queue status=canceled location=worker-selected"));
        Assert.Contains(fixture.Logs, line => line.Contains("delivered=False"));
    }

    [Fact]
    public async Task CancelledQueuedAudioIsNeverSentToTheWorker()
    {
        await using var fixture = new Fixture();
        var finish = Signal();
        fixture.Create = _ => new Worker { Respond = async (_, token) => { await finish.Task.WaitAsync(token); return "active"; } };
        await using var lease = await fixture.Acquire();
        using var audio = fixture.Audio("active");
        using var queuedAudio = fixture.Audio("cancelled");
        var active = Request(lease, audio);
        await Eventually(() => fixture.Workers[0].Inputs.Count == 1);
        using var cancellation = new CancellationTokenSource();
        var queued = Request(lease, queuedAudio, token: cancellation.Token);
        await Eventually(() => lease.QueuedRequests == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        finish.SetResult();
        await active;
        await lease.DisposeAsync();
        Assert.Single(fixture.Workers[0].Inputs);
        Assert.Contains(fixture.Logs, line => line.Contains("stage=model-queue status=canceled location=queued"));
        Assert.Single(fixture.Logs, line => line.Contains("stage=model-queue status=dequeue waitMs="));
    }

    [Fact]
    public async Task LastReleaseCancelsNativeWorkAndWaitsForItBeforeDisposing()
    {
        await using var fixture = new Fixture();
        fixture.Create = _ => new Worker { Respond = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return "unused"; } };
        await using var lease = await fixture.Acquire();
        using var audio = fixture.Audio("active");
        var active = Request(lease, audio);
        await Eventually(() => fixture.Workers[0].Inputs.Count == 1);
        await lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.True(fixture.Workers[0].Disposed);
        Assert.Equal(0, fixture.Workers[0].ActiveRequestsAtDispose);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTimedOutWorkerIsRebuiltForTheNextRequest(bool timeout)
    {
        await using var fixture = new Fixture(TimeSpan.FromMilliseconds(150));
        fixture.Create = _ => fixture.Workers.Count == 0 ? new Worker { Respond = async (_, token) =>
        {
            if (timeout) await Task.Delay(Timeout.Infinite, token);
            throw new IOException("worker died");
        }} : new Worker();
        await using var lease = await fixture.Acquire();
        using var audio = fixture.Audio("recovered");
        var failed = Request(lease, audio);
        if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => failed);
        else await Assert.ThrowsAsync<IOException>(() => failed);
        Assert.Equal("recovered", (await Request(lease, audio)).Text);
        Assert.Equal(2, fixture.Workers.Count);
        Assert.True(fixture.Workers[0].Disposed);
    }

    [Fact]
    public async Task NativeProtocolConsumesCancelledResponseBeforeServingTheNextClient()
    {
        await using var fixture = new Fixture();
        var script = Path.Combine(fixture.DirectoryPath, "protocol.cmd");
        var started = Path.Combine(fixture.DirectoryPath, "started");
        var finish = Path.Combine(fixture.DirectoryPath, "finish");
        // Exercise the actual redirected-pipe protocol without downloading a model.
        File.WriteAllText(script,
            $"@echo off\r\necho READY\r\nset /p request=\r\necho started>\"{started}\"\r\n" +
            $":wait\r\nif exist \"{finish}\" goto result\r\nping -n 2 127.0.0.1 > nul\r\ngoto wait\r\n" +
            ":result\r\necho RESULT\tc3RhbGU=\r\nset /p request=\r\necho RESULT\tdm9pY2U=\r\nset /p request=\r\n");
        await using var pool = new SenseVoiceModelPool((_, _, token) => new SenseVoiceResidentWorker(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", script], fixture.DirectoryPath, token),
            TimeSpan.FromSeconds(10));
        await using var subtitle = await pool.AcquireAsync(fixture.Configuration, fixture.Log, CancellationToken.None);
        await using var voice = await pool.AcquireAsync(fixture.Configuration, fixture.Log, CancellationToken.None);
        using var audio = fixture.Audio("request");
        using var cancellation = new CancellationTokenSource();
        var cancelled = Request(subtitle, audio, token: cancellation.Token);
        await Eventually(() => File.Exists(started));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = Request(voice, audio, SenseVoiceRequestPriority.Interactive);
        File.WriteAllText(finish, "continue");
        Assert.Equal("voice", (await next.WaitAsync(TimeSpan.FromSeconds(5))).Text);
    }

    [Fact]
    public async Task SubtitleStopAndRestartReuseTheVoiceInputModel()
    {
        await using var fixture = new Fixture();
        fixture.Create = _ => new Worker { Respond = (_, _) => Task.FromResult("recognized speech") };
        await using var voice = await Task.Run(() => new SenseVoiceCommandTranscriber(fixture.Configuration, fixture.Log,
            CancellationToken.None, SenseVoiceRequestPriority.Interactive, fixture.Pool));
        var configuration = new AppConfiguration { Speech = fixture.Configuration };
        configuration.Subtitles.TranslateText = false;
        configuration.Subtitles.Diarization.Enabled = false;
        using var http = new HttpClient();
        Capture? capture = null;
        await using var subtitles = new SubtitleSessionController(configuration, fixture.Log, http,
            _ => capture = new Capture(), (speech, token) => new SenseVoiceCommandTranscriber(speech, fixture.Log, token,
                SenseVoiceRequestPriority.Subtitle, fixture.Pool), () => true, TimeSpan.FromMilliseconds(10));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await subtitles.StartListeningAsync(() => 123);
            await Eventually(() => subtitles.ListeningState == SubtitleListeningState.Listening);
            capture!.Emit();
            await Eventually(() => subtitles.History.Entries.Count == attempt + 1);
            await subtitles.StopListeningAsync();
            Assert.Single(fixture.Workers);
            Assert.False(fixture.Workers[0].Disposed);
        }
        await voice.DisposeAsync();
        Assert.True(fixture.Workers[0].Disposed);
    }

    private static Task<CommandTranscriptionResult> Request(SenseVoiceModelPool.Lease lease, CommandAudioInput audio,
        SenseVoiceRequestPriority priority = SenseVoiceRequestPriority.Subtitle, CancellationToken token = default) =>
        lease.TranscribeAsync(audio, "[shared-test]", priority, token);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "sensevoice-pool-" + Guid.NewGuid().ToString("N"));
        public AppLog Log { get; }
        public ConcurrentQueue<string> Logs { get; } = new();
        public SenseVoiceModelPool Pool { get; }
        public SpeechConfiguration Configuration { get; } = new();
        public Func<CancellationToken, Worker> Create { get; set; } = _ => new();
        private readonly ConcurrentQueue<Worker> _workers = new();
        public IReadOnlyList<Worker> Workers => _workers.ToArray();
        public Fixture(TimeSpan? requestTimeout = null)
        {
            Log = new AppLog(DirectoryPath);
            Log.MessageWritten += (_, line) => Logs.Enqueue(line);
            Pool = new((_, _, token) => { var worker = Create(token); _workers.Enqueue(worker); return worker; }, requestTimeout);
        }
        public Task<SenseVoiceModelPool.Lease> Acquire(CancellationToken token = default) => Pool.AcquireAsync(Configuration, Log, token);
        public CommandAudioInput Audio(string text)
        {
            var path = Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".wav");
            File.WriteAllText(path, text);
            return new(path, TimeSpan.FromSeconds(1));
        }
        public async ValueTask DisposeAsync() { await Pool.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }

    private sealed class Worker : ISenseVoiceWorker
    {
        private int _active;
        public ConcurrentQueue<string> Inputs { get; } = new();
        public ConcurrentQueue<string> Paths { get; } = new();
        public Func<string, CancellationToken, Task<string>> Respond { get; set; } = File.ReadAllTextAsync;
        public CancellationToken LastToken { get; private set; }
        public int MaximumConcurrentRequests { get; private set; }
        public int ActiveRequestsAtDispose { get; private set; }
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public Action? OnDispose { get; init; }
        public int ProcessId => 123;
        public async Task<string> TranscribeAsync(string audioPath, CancellationToken token)
        {
            Assert.False(Disposed);
            MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, Interlocked.Increment(ref _active));
            LastToken = token;
            Paths.Enqueue(audioPath);
            try { Inputs.Enqueue(await File.ReadAllTextAsync(audioPath, token)); return await Respond(audioPath, token); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public string TakeDiagnostics() => "";
        public void Dispose() { OnDispose?.Invoke(); Disposed = true; DisposeCount++; ActiveRequestsAtDispose = Volatile.Read(ref _active); }
    }

    private sealed class Capture : ISubtitleAudioCapture
    {
        private readonly TaskCompletionSource _completion = Signal();
        public event EventHandler<ProcessLoopbackAudioSegment>? SegmentReady;
        public Task Completion => _completion.Task;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public void Emit() => SegmentReady?.Invoke(this, new(new byte[16000], new WaveFormat(16000, 16, 1), TimeSpan.Zero, TimeSpan.FromSeconds(.5)));
        public ValueTask DisposeAsync() { _completion.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
