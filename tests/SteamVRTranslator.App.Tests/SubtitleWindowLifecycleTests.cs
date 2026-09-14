using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.Speech;
using SteamVRTranslator.App.Subtitles;
using Xunit;

namespace SteamVRTranslator.App.Tests;

[Collection(MainWindowTestCollection.Name)]
public sealed class SubtitleWindowLifecycleTests
{
    [Fact]
    public async Task ClosingManagerAwaitsTheSubtitleSessionWithoutBlockingItsDispatcher()
    {
        await OnDispatcherAsync(async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "subtitle-window-" + Guid.NewGuid().ToString("N"));
            using var http = new HttpClient();
            var window = new MainWindow();
            var worker = new Worker();
            var configuration = new AppConfiguration();
            configuration.Subtitles.Diarization.Enabled = false;
            await using var session = new SubtitleSessionController(configuration, new AppLog(directory), http,
                _ => throw new InvalidOperationException("No scene is running"), (_, _) => worker, () => true);
            typeof(MainWindow).GetField("_subtitleSession", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            await session.StartListeningAsync(() => 0);
            while (session.ListeningState != SubtitleListeningState.WaitingForProcess) await Task.Delay(10);
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(session.IsListening);
            Assert.True(worker.Disposed);
            Directory.Delete(directory, true);
        });
    }

    [Fact]
    public async Task FailedStartupOffersStartAndStoppingDisablesRepeatedClicks()
    {
        await OnDispatcherAsync(() =>
        {
            var window = new SubtitleHistoryWindow(new SubtitleHistoryViewModel(new SubtitleConfiguration()));
            try
            {
                var text = Assert.IsType<TextBlock>(window.FindName("ListenButtonText"));
                var button = Assert.IsType<Button>(window.FindName("ListenButton"));
                window.ApplyListeningState(SubtitleListeningState.Error, "failed", isListening: false);
                Assert.Equal(AppLocalization.Text("Subtitle.Window.StartListening"), text.Text);
                Assert.True(button.IsEnabled);
                window.ApplyListeningState(SubtitleListeningState.Stopping, isListening: true);
                Assert.False(button.IsEnabled);
                window.ApplyListeningState(SubtitleListeningState.Stopped, isListening: false);
                Assert.True(button.IsEnabled);
            }
            finally { window.ClosePermanently(); }
            return Task.CompletedTask;
        });
    }

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Worker : ISubtitleLocalTranscriber
    {
        public bool Disposed { get; private set; }
        public Task<CommandTranscriptionResult> TranscribeAsync(CommandAudioInput audio, string tag, CancellationToken token) =>
            Task.FromResult(new CommandTranscriptionResult("test", TimeSpan.Zero));
        public void Dispose() => Disposed = true;
    }
}
