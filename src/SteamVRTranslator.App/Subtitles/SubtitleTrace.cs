using System.Diagnostics;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Speech;

namespace SteamVRTranslator.App.Subtitles;

/// <summary>Correlates lifecycle and asynchronous stages without logging speech or credentials.</summary>
internal sealed class SubtitleTrace(AppLog log, string kind)
{
    public string Tag { get; } = $"[subtitles] [session={kind}-{Guid.NewGuid():N}]";
    public void Info(string message) => log.Info($"{Tag} {message}");

    public static async Task<T> MeasureAsync<T>(AppLog log, string tag, string stage,
        Func<Task<T>> operation, Func<T, string>? describe = null)
    {
        var timer = Stopwatch.StartNew();
        log.Info($"{tag} stage={stage} status=start");
        try
        {
            var result = await operation().ConfigureAwait(false);
            log.Info($"{tag} stage={stage} status=complete elapsedMs={timer.Elapsed.TotalMilliseconds:F0} {describe?.Invoke(result)}");
            return result;
        }
        catch (NoSpeechRecognizedException)
        {
            log.Info($"{tag} stage={stage} status=no-speech elapsedMs={timer.Elapsed.TotalMilliseconds:F0}");
            throw;
        }
        catch (OperationCanceledException)
        {
            log.Info($"{tag} stage={stage} status=canceled elapsedMs={timer.Elapsed.TotalMilliseconds:F0}");
            throw;
        }
        catch (Exception exception)
        {
            log.Error($"{tag} stage={stage} status=failed elapsedMs={timer.Elapsed.TotalMilliseconds:F0}", exception);
            throw;
        }
    }

    public static Task MeasureAsync(AppLog log, string tag, string stage, Func<Task> operation) =>
        MeasureAsync(log, tag, stage, async () => { await operation().ConfigureAwait(false); return true; });
}
