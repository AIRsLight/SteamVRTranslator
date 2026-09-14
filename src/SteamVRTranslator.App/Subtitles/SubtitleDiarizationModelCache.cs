using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

internal sealed record SubtitleDiarizationModelKey(string SegmentationPath, string EmbeddingPath, int Threads)
{
    public static SubtitleDiarizationModelKey From(SubtitleDiarizationConfiguration configuration) => new(
        Path.GetFullPath(configuration.SegmentationModelPath, AppContext.BaseDirectory).ToUpperInvariant(),
        Path.GetFullPath(configuration.EmbeddingModelPath, AppContext.BaseDirectory).ToUpperInvariant(),
        Math.Clamp(configuration.CpuThreadCount, 1, Math.Max(1, Environment.ProcessorCount)));
}

internal interface ISubtitleDiarizationModels : IDisposable
{
    IReadOnlyList<SubtitleSpeakerSegment> Segment(float[] samples, double threshold);
    float[]? Embed(float[] samples);
}

/// <summary>Subtitle-owned models. Audio operations are serialized; speaker identities belong to callers.</summary>
internal sealed class SubtitleDiarizationModelCache(AppLog log,
    Func<SubtitleDiarizationModelKey, CancellationToken, ISubtitleDiarizationModels> create) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<SubtitleDiarizationModelKey, Entry> _entries = [];
    private readonly object _disposeSync = new();
    private SubtitleDiarizationModelKey? _preferred;
    private Task? _disposal;
    private volatile bool _disposed;

    public void UpdateConfiguration(SubtitleDiarizationConfiguration? configuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var preferred = configuration?.Enabled == true ? SubtitleDiarizationModelKey.From(configuration) : null;
        if (Volatile.Read(ref _preferred) == preferred) return;
        Volatile.Write(ref _preferred, preferred);
        _ = TrimAsync();
    }

    public Task<Lease> AcquireAsync(SubtitleDiarizationConfiguration configuration, CancellationToken token)
    {
        var key = SubtitleDiarizationModelKey.From(configuration);
        return RunAsync(() =>
        {
            if (!_entries.TryGetValue(key, out var entry)) _entries.Add(key, entry = new Entry(key));
            EnsureModels(entry, token);
            token.ThrowIfCancellationRequested();
            entry.References++;
            return new Lease(this, entry);
        }, token);
    }

    private ISubtitleDiarizationModels EnsureModels(Entry entry, CancellationToken token)
    {
        if (entry.Models is null)
        {
            token.ThrowIfCancellationRequested();
            entry.Models = create(entry.Key, token);
            log.Info($"[subtitles] 字幕专用分段与声纹模型已常驻：线程={entry.Key.Threads}。");
        }
        return entry.Models;
    }

    private Task<T> RunAsync<T>(Func<T> action, CancellationToken token) => Task.Run(async () =>
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested(); return action(); }
        finally { _gate.Release(); }
    }, token);

    private Task<T> ProcessAsync<T>(Lease lease, Func<ISubtitleDiarizationModels, T> action, CancellationToken token) => RunAsync(() =>
    {
        ObjectDisposedException.ThrowIf(lease.IsDisposed, lease);
        var entry = lease.Entry;
        try
        {
            var result = action(EnsureModels(entry, token));
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { ReleaseModels(entry); throw; }
    }, token);

    private Task ReleaseAsync(Entry entry) => Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.References--;
            TrimIdle(force: false);
        }
        finally { _gate.Release(); }
    });

    private async Task TrimAsync()
    {
        try
        {
            await Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { if (!_disposed) TrimIdle(force: false); }
                finally { _gate.Release(); }
            }).ConfigureAwait(false);
        }
        catch (Exception exception) { log.Error("[subtitles] 清理旧说话人模型失败。", exception); }
    }

    public Task ReleaseIdleAsync() => RunAsync(() => { TrimIdle(force: true); return true; }, CancellationToken.None);

    private void TrimIdle(bool force)
    {
        var preferred = Volatile.Read(ref _preferred);
        foreach (var entry in _entries.Values.Where(entry => entry.References == 0 && (force || entry.Key != preferred)).ToArray())
        {
            ReleaseModels(entry);
            _entries.Remove(entry.Key);
        }
    }

    private void ReleaseModels(Entry entry)
    {
        var models = entry.Models;
        entry.Models = null;
        if (models is null) return;
        try { models.Dispose(); }
        catch (Exception exception) { log.Error("[subtitles] 释放说话人模型失败。", exception); }
        finally { log.Info("[subtitles] 已释放字幕专用分段与声纹模型。"); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposed = true;
            return new(_disposal ??= Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var entry in _entries.Values) ReleaseModels(entry);
                    _entries.Clear();
                }
                finally { _gate.Release(); }
            }));
        }
    }

    internal sealed class Entry(SubtitleDiarizationModelKey key)
    {
        public SubtitleDiarizationModelKey Key { get; } = key;
        public ISubtitleDiarizationModels? Models { get; set; }
        public int References { get; set; }
    }

    internal sealed class Lease(SubtitleDiarizationModelCache owner, Entry entry) : IAsyncDisposable
    {
        private readonly object _sync = new();
        private Task? _disposal;
        internal Entry Entry { get; } = entry;
        public SubtitleDiarizationModelKey Key => Entry.Key;
        internal bool IsDisposed { get { lock (_sync) return _disposal is not null; } }
        public Task<T> ProcessAsync<T>(Func<ISubtitleDiarizationModels, T> action, CancellationToken token) => owner.ProcessAsync(this, action, token);
        public ValueTask DisposeAsync()
        {
            lock (_sync) return new(_disposal ??= owner.ReleaseAsync(Entry));
        }
    }
}
