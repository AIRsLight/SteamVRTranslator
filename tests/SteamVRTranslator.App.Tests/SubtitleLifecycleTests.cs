using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

public sealed class SubtitleLifecycleTests
{
    [Fact]
    public async Task DetailedLogsCorrelateAudioThroughTranslationAndHistoryWithoutSpeechOrKeys()
    {
        await using var fixture = new Fixture();
        fixture.Configuration.Subtitles.TranslateText = true;
        fixture.Configuration.Translation.Providers[0].ApiKey = "secret-translation-key";
        fixture.Configuration.Subtitles.VibeVoiceApiKey = "secret-asr-key";
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await Eventually(() => fixture.Logs.Any(line => line.Contains("stage=segment-process status=complete")));
        await fixture.Controller.StopListeningAsync();
        var lines = fixture.Logs.ToArray();
        var session = SessionTag(Assert.Single(lines, line => line.Contains("stage=session status=start")));
        foreach (var marker in new[] { "stage=queue status=enqueue", "stage=queue status=dequeue", "stage=audio-read status=complete",
            "stage=audio-analyze status=complete", "stage=asr status=complete", "stage=history-add status=complete",
            "stage=translation-slot status=complete", "stage=translation-request status=complete", "stage=history-translation status=complete",
            "stage=translation status=complete", "stage=segment-process status=complete" })
        {
            var line = Assert.Single(lines, line => line.Contains(marker));
            Assert.Contains(session, line);
            Assert.Contains("[segment=000001]", line);
        }
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("test speech", line);
            Assert.DoesNotContain("secret-translation-key", line);
            Assert.DoesNotContain("secret-asr-key", line);
        });
        Assert.Contains(lines, line => line.Contains($"entry={fixture.Controller.History.Entries[0].Id}") && line.Contains("stage=history-translation status=complete"));
        Assert.Contains(lines, line => line.Contains("received=1 processed=1 dropped=0 abandoned=0 canceled=0 failed=0"));
    }

    [Fact]
    public async Task QueueOverflowAndStopAccountForEverySegment()
    {
        await using var fixture = new Fixture();
        fixture.CreateLocal = (_, _) => new Transcriber { OnTranscribe = token => Task.Delay(Timeout.Infinite, token) };
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await fixture.Transcribers[0].Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for (var index = 0; index < 6; index++) fixture.Captures[0].Emit();
        await fixture.Controller.StopListeningAsync();
        var dropped = fixture.Logs.Where(line => line.Contains("stage=queue status=dropped")).ToArray();
        Assert.Equal(2, dropped.Length);
        Assert.Contains("[segment=000002]", dropped[0]);
        Assert.Contains("[segment=000003]", dropped[1]);
        Assert.Equal(4, fixture.Logs.Count(line => line.Contains("stage=queue status=abandoned")));
        Assert.Contains(fixture.Logs, line => line.Contains("[segment=000001]") && line.Contains("stage=asr status=canceled"));
        Assert.Contains(fixture.Logs, line => line.Contains("received=7 processed=0 dropped=2 abandoned=4 canceled=1 failed=0"));
        Assert.Empty(fixture.Controller.History.Entries);
    }

    [Fact]
    public async Task ReconnectionKeepsSessionButRestartAndReplayGetIndependentIdentifiers()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Captures[0].Fail();
        await Eventually(() => fixture.Captures.Count == 2 && fixture.Controller.ListeningState == SubtitleListeningState.Listening);
        var path = Path.Combine(fixture.DirectoryPath, "replay.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1))) writer.Write(new byte[16000], 0, 16000);
        await fixture.Controller.ReplayFileAsync(path, null, CancellationToken.None);
        await fixture.Controller.StopListeningAsync();
        await fixture.StartAsync();
        var sessions = fixture.Logs.Where(line => line.Contains("stage=session status=start")).Select(SessionTag).ToArray();
        Assert.Equal(2, sessions.Distinct().Count());
        Assert.Contains(fixture.Logs, line => line.Contains(sessions[0]) && line.Contains("[capture=2]") && line.Contains("stage=capture-connect status=complete"));
        var replay = SessionTag(Assert.Single(fixture.Logs, line => line.Contains("stage=replay status=start")));
        Assert.DoesNotContain(replay, sessions);
        Assert.Contains(fixture.Logs, line => line.Contains(replay) && line.Contains("stage=history-add status=complete"));
    }

    [Fact]
    public async Task TranslationFailureHasATerminalLogAndClearsTheHistorySpinner()
    {
        await using var fixture = new Fixture();
        fixture.Configuration.Subtitles.TranslateText = true;
        fixture.Configuration.Translation.Providers.Clear();
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await Eventually(() => fixture.Logs.Any(line => line.Contains("stage=segment-process status=complete")));
        var entry = Assert.Single(fixture.Controller.History.Entries);
        Assert.False(entry.IsTranslating);
        Assert.False(string.IsNullOrWhiteSpace(entry.Error));
        Assert.Contains(fixture.Logs, line => line.Contains("[segment=000001]") && line.Contains("stage=translation status=failed"));
        Assert.Contains(fixture.Logs, line => line.Contains("stage=history-translation-finish status=complete"));
        Assert.True(fixture.Controller.IsListening);
    }

    [Fact]
    public async Task WaitingForAProcessLogsOnlyStateChanges()
    {
        await using var fixture = new Fixture();
        var polls = 0;
        await fixture.Controller.StartListeningAsync(() => { Interlocked.Increment(ref polls); return 0; });
        await Eventually(() => Volatile.Read(ref polls) >= 6);
        Assert.Single(fixture.Logs, line => line.Contains("to=WaitingForProcess"));
        Assert.Empty(fixture.Captures);
    }

    private static string SessionTag(string line) => Regex.Match(line, @"\[session=[^\]]+\]").Value;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyOrCanceledTranslationIsDistinguishedFromSuccess(bool cancel)
    {
        var handler = new ControlledTranslationHandler(cancel);
        await using var fixture = new Fixture(handler);
        fixture.Configuration.Subtitles.TranslateText = true;
        fixture.Configuration.Translation.ActiveProviderId = "test";
        fixture.Configuration.Translation.Providers = [new TranslationProviderConfiguration
        {
            Id = "test", Model = "test-model", BaseUrl = "https://translation.invalid/v1", ApiKey = "private-test-key"
        }];
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancel) await fixture.Controller.StopListeningAsync().WaitAsync(TimeSpan.FromSeconds(3));
        else await Eventually(() => fixture.Logs.Any(line => line.Contains("stage=segment-process status=complete")));
        var entry = Assert.Single(fixture.Controller.History.Entries);
        Assert.False(entry.IsTranslating);
        Assert.Empty(entry.TranslatedText);
        if (!cancel) Assert.False(string.IsNullOrWhiteSpace(entry.Error));
        Assert.Contains(fixture.Logs, line => line.Contains("[segment=000001]") && line.Contains($"stage=translation status={(cancel ? "canceled" : "empty")}"));
        Assert.Contains(fixture.Logs, line => line.Contains($"stage=translation-request status={(cancel ? "canceled" : "complete")}"));
        Assert.DoesNotContain(fixture.Logs, line => line.Contains("stage=translation status=complete") || line.Contains("private-test-key"));
    }

    [Fact]
    public async Task StopDuringActivationWaitsForCleanupAndSerializesRestart()
    {
        await using var fixture = new Fixture();
        var activation = NewSignal();
        var first = new Capture { OnStart = _ => activation.Task };
        fixture.CreateCapture = _ => fixture.Captures.Count == 0 ? first : new Capture();
        await fixture.Controller.StartListeningAsync(() => 123);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = fixture.Controller.StopListeningAsync();
        var restart = fixture.Controller.StartListeningAsync(() => 456);
        Assert.False(stop.IsCompleted);
        Assert.False(restart.IsCompleted);
        Assert.False(first.Disposed);
        activation.SetResult();
        await Task.WhenAll(stop, restart).WaitAsync(TimeSpan.FromSeconds(3));
        await Eventually(() => fixture.Captures.Count == 2 && fixture.Controller.ListeningState == SubtitleListeningState.Listening);
        Assert.True(first.Disposed);
        Assert.Equal(1, first.DisposeCount);
        Assert.DoesNotContain(fixture.States, state => state.State == SubtitleListeningState.Listening && state.ProcessId == 123);
        Assert.True(fixture.Controller.IsListening);
    }

    [Fact]
    public async Task NativeCaptureFailureReconnectsToTheSameProcess()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        var first = fixture.Captures[0];
        first.Fail();
        await Eventually(() => fixture.Captures.Count == 2 && fixture.Controller.ListeningState == SubtitleListeningState.Listening);
        Assert.True(first.Disposed);
        Assert.Contains(fixture.States, state => state.State == SubtitleListeningState.Error);
        Assert.True(fixture.Controller.IsListening);
    }

    [Fact]
    public async Task ModelStartupFailureReturnsToStartableStateAndCanRetry()
    {
        await using var fixture = new Fixture();
        fixture.CreateLocal = (_, _) => throw new IOException("model startup failed");
        await fixture.Controller.StartListeningAsync(() => 123);
        await Eventually(() => fixture.Controller.ListeningState == SubtitleListeningState.Error && !fixture.Controller.IsListening);
        Assert.Empty(fixture.Captures);
        fixture.CreateLocal = (_, _) => new Transcriber();
        await fixture.StartAsync();
        Assert.Single(fixture.Captures);
        Assert.True(fixture.Controller.IsListening);
    }

    [Fact]
    public async Task StartingTheModelDoesNotBlockStartOrCancellation()
    {
        await using var fixture = new Fixture();
        var preparing = NewSignal();
        fixture.CreateLocal = (_, token) =>
        {
            preparing.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return new Transcriber();
        };
        await fixture.Controller.StartListeningAsync(() => 123).WaitAsync(TimeSpan.FromSeconds(1));
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Controller.StopListeningAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(fixture.Controller.IsListening);
        Assert.Empty(fixture.Captures);
    }

    [Fact]
    public async Task SettingsChangesWaitForRecognitionBeforeReplacingTheWorker()
    {
        await using var fixture = new Fixture();
        var recognition = NewSignal();
        var first = new Transcriber { OnTranscribe = token => recognition.Task.WaitAsync(token) };
        fixture.CreateLocal = (_, _) => fixture.Transcribers.Count == 0 ? first : new Transcriber();
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await first.Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Configuration.Speech.EffectiveRecognitionLanguage = "ja";
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        Assert.False(first.Disposed);
        recognition.SetResult();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        fixture.Captures[0].Emit();
        await Eventually(() => fixture.Transcribers.Count == 2 && fixture.Controller.History.Entries.Count == 2);
        Assert.True(first.Disposed);
        Assert.Equal(0, first.ActiveRequestsAtDispose);
        Assert.True(fixture.Controller.IsListening);
    }

    [Fact]
    public async Task DisplayOnlyChangesKeepTheWorkerAndApplyAtTheNextSegment()
    {
        await using var fixture = new Fixture();
        await fixture.StartAsync();
        fixture.Configuration.Subtitles.ShowOriginalText = false;
        fixture.Controller.ApplyConfiguration(fixture.Configuration);
        fixture.Captures[0].Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        Assert.Single(fixture.Transcribers);
        Assert.Equal(string.Empty, fixture.Controller.History.Entries[0].SourceText);
        Assert.False(fixture.Transcribers[0].Disposed);
    }

    [Fact]
    public async Task CancellationFinishesRecognitionBeforeDisposalAndRestartUsesAFreshWorker()
    {
        await using var fixture = new Fixture();
        var first = new Transcriber { OnTranscribe = token => Task.Delay(Timeout.Infinite, token) };
        fixture.CreateLocal = (_, _) => fixture.Transcribers.Count == 0 ? first : new Transcriber();
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await first.Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(fixture.Controller.StopListeningAsync(), fixture.Controller.StopListeningAsync())
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(first.Disposed);
        Assert.Equal(0, first.ActiveRequestsAtDispose);
        Assert.Equal(1, first.DisposeCount);
        await fixture.StartAsync();
        fixture.Captures[1].Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        Assert.Equal(2, fixture.Transcribers.Count);
    }

    [Fact]
    public async Task RecognitionFailureStopsCaptureAndAllowsAFreshSession()
    {
        await using var fixture = new Fixture();
        fixture.CreateLocal = (_, _) => new Transcriber
        {
            OnTranscribe = _ => Task.FromException(new IOException("worker exited"))
        };
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await Eventually(() => fixture.Controller.ListeningState == SubtitleListeningState.Error && !fixture.Controller.IsListening);
        Assert.True(fixture.Captures[0].Disposed);
        Assert.True(fixture.Transcribers[0].Disposed);
        fixture.CreateLocal = (_, _) => new Transcriber();
        await fixture.StartAsync();
        fixture.Captures[1].Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        Assert.Equal(2, fixture.Transcribers.Count);
    }

    [Fact]
    public async Task SilentSegmentsDoNotTurnListeningIntoAnError()
    {
        await using var fixture = new Fixture();
        var requests = 0;
        fixture.CreateLocal = (_, _) => new Transcriber
        {
            OnTranscribe = _ => Interlocked.Increment(ref requests) == 1
                ? Task.FromException(new NoSpeechRecognizedException("no speech")) : Task.CompletedTask
        };
        await fixture.StartAsync();
        fixture.Captures[0].Emit();
        await Eventually(() => Volatile.Read(ref requests) == 1);
        fixture.Captures[0].Emit();
        await Eventually(() => fixture.Controller.History.Entries.Count == 1);
        Assert.DoesNotContain(fixture.States, state => state.State == SubtitleListeningState.Error);
        Assert.Single(fixture.Transcribers);
    }

    [Fact]
    public async Task ExternalCancellationReleasesCaptureAndWorkerWithoutAnExplicitStop()
    {
        await using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await fixture.Controller.StartListeningAsync(() => 123, cancellation.Token);
        await Eventually(() => fixture.Controller.ListeningState == SubtitleListeningState.Listening);
        cancellation.Cancel();
        await Eventually(() => !fixture.Controller.IsListening);
        Assert.True(fixture.Captures[0].Disposed);
        Assert.True(fixture.Transcribers[0].Disposed);
        Assert.Equal(SubtitleListeningState.Stopped, fixture.Controller.ListeningState);
    }

    [Fact]
    public async Task StopAllCancelsReplayAndLiveCaptureAndTheirWorkersAreIndependent()
    {
        await using var fixture = new Fixture();
        fixture.CreateLocal = (_, _) => new Transcriber { OnTranscribe = token => Task.Delay(Timeout.Infinite, token) };
        await fixture.StartAsync();
        var path = Path.Combine(fixture.DirectoryPath, "replay.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
            writer.Write(new byte[16000], 0, 16000);
        var replay = fixture.Controller.ReplayFileAsync(path, null, CancellationToken.None);
        await Eventually(() => fixture.Transcribers.Count == 2);
        await fixture.Transcribers[1].Requested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Controller.StopAllAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
        Assert.All(fixture.Transcribers, worker => Assert.True(worker.Disposed));
        Assert.True(fixture.Captures[0].Disposed);
        Assert.False(fixture.Controller.IsListening);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "subtitle-lifecycle-" + Guid.NewGuid().ToString("N"));
        public AppConfiguration Configuration { get; } = new();
        private readonly ConcurrentQueue<Capture> _captures = new();
        private readonly ConcurrentQueue<Transcriber> _transcribers = new();
        public IReadOnlyList<Capture> Captures => _captures.ToArray();
        public IReadOnlyList<Transcriber> Transcribers => _transcribers.ToArray();
        public ConcurrentQueue<SubtitleListeningStateChangedEventArgs> States { get; } = new();
        public ConcurrentQueue<string> Logs { get; } = new();
        public Func<uint, Capture> CreateCapture { get; set; } = _ => new Capture();
        public Func<SpeechConfiguration, CancellationToken, Transcriber> CreateLocal { get; set; } = (_, _) => new Transcriber();
        private readonly HttpClient _http;
        public SubtitleSessionController Controller { get; }
        public Fixture(HttpMessageHandler? handler = null)
        {
            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            Configuration.Subtitles.TranslateText = false;
            Configuration.Subtitles.Diarization.Enabled = false;
            var log = new AppLog(DirectoryPath);
            log.MessageWritten += (_, line) => Logs.Enqueue(line);
            Controller = new SubtitleSessionController(Configuration, log, _http, id =>
            {
                var capture = CreateCapture(id); _captures.Enqueue(capture); return capture;
            }, (speech, token) =>
            {
                var worker = CreateLocal(speech, token); _transcribers.Enqueue(worker); return worker;
            }, () => true, TimeSpan.FromMilliseconds(20));
            Controller.ListeningStateChanged += (_, state) => States.Enqueue(state);
        }
        public async Task StartAsync()
        {
            await Controller.StartListeningAsync(() => 123);
            await Eventually(() => Controller.ListeningState == SubtitleListeningState.Listening);
        }
        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync(); _http.Dispose(); Directory.Delete(DirectoryPath, true);
        }
    }

    private sealed class Capture : ISubtitleAudioCapture
    {
        private readonly TaskCompletionSource _completion = NewSignal();
        public TaskCompletionSource Started { get; } = NewSignal();
        public Func<CancellationToken, Task> OnStart { get; set; } = _ => Task.CompletedTask;
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<ProcessLoopbackAudioSegment>? SegmentReady;
        public Task Completion => _completion.Task;
        public async Task StartAsync(CancellationToken token) { Started.TrySetResult(); await OnStart(token); }
        public void Fail() => _completion.TrySetException(new IOException("capture disconnected"));
        public void Emit() => SegmentReady?.Invoke(this,
            new ProcessLoopbackAudioSegment(new byte[16000], new WaveFormat(16000, 16, 1), TimeSpan.Zero, TimeSpan.FromSeconds(0.5)));
        public ValueTask DisposeAsync() { Disposed = true; DisposeCount++; _completion.TrySetResult(); return ValueTask.CompletedTask; }
    }

    private sealed class ControlledTranslationHandler(bool waitForCancellation) : HttpMessageHandler
    {
        public TaskCompletionSource Requested { get; } = NewSignal();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requested.TrySetResult();
            if (waitForCancellation) await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"\"}}]}")
            };
        }
    }

    private sealed class Transcriber : ISubtitleLocalTranscriber
    {
        private int _active;
        public TaskCompletionSource Requested { get; } = NewSignal();
        public Func<CancellationToken, Task> OnTranscribe { get; set; } = _ => Task.CompletedTask;
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public int ActiveRequestsAtDispose { get; private set; }
        public async Task<CommandTranscriptionResult> TranscribeAsync(CommandAudioInput audio, string tag, CancellationToken token)
        {
            Assert.False(Disposed);
            Interlocked.Increment(ref _active);
            Requested.TrySetResult();
            try { await OnTranscribe(token); return new("test speech", TimeSpan.Zero); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() { Disposed = true; DisposeCount++; ActiveRequestsAtDispose = Volatile.Read(ref _active); }
    }
}
