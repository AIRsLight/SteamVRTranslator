using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

public sealed class SubtitleAudioProcessor : IAsyncDisposable
{
    public const int SampleRate = 16000;
    private readonly AppLog _log;
    private readonly SubtitleDiarizationModelCache _models;

    public SubtitleAudioProcessor(AppLog log) : this(log, (key, token) => new ResidentSubtitleDiarizationModels(key, token)) { }

    internal SubtitleAudioProcessor(AppLog log, Func<SubtitleDiarizationModelKey, CancellationToken, ISubtitleDiarizationModels> create)
    {
        _log = log;
        _models = new SubtitleDiarizationModelCache(log, create);
    }

    internal void UpdateConfiguration(SubtitleDiarizationConfiguration? configuration) => _models.UpdateConfiguration(configuration);
    internal Task<SubtitleDiarizationModelCache.Lease> AcquireModelsAsync(SubtitleDiarizationConfiguration configuration, CancellationToken token) =>
        _models.AcquireAsync(configuration, token);
    internal Task ReleaseIdleModelsAsync() => _models.ReleaseIdleAsync();
    public ValueTask DisposeAsync() => _models.DisposeAsync();

    public async Task<SubtitleAudioDocument> AnalyzeAsync(
        string audioPath,
        SubtitleDiarizationConfiguration configuration,
        SpeakerIdentityRegistry speakerRegistry,
        CancellationToken cancellationToken)
    {
        await using var lease = configuration.Enabled ? await AcquireModelsAsync(configuration, cancellationToken).ConfigureAwait(false) : null;
        return await AnalyzeAsync(audioPath, configuration, speakerRegistry, lease, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SubtitleAudioDocument> AnalyzeAsync(
        string audioPath, SubtitleDiarizationConfiguration configuration, SpeakerIdentityRegistry speakerRegistry,
        SubtitleDiarizationModelCache.Lease? lease, CancellationToken cancellationToken, string tag = "[subtitles]")
    {
        var samples = await SubtitleTrace.MeasureAsync(_log, tag, "audio-read", () => Task.Run(
            () => ReadMonoSamples(audioPath, cancellationToken),
            cancellationToken), samples => $"samples={samples.Length} sampleRate={SampleRate} channels=1");
        if (samples.Length == 0)
        {
            throw new InvalidOperationException("音频文件没有可识别的采样数据。");
        }

        var duration = TimeSpan.FromSeconds((double)samples.Length / SampleRate);
        if (!configuration.Enabled)
        {
            _log.Info($"{tag} stage=diarization status=skipped reason=disabled");
            return new SubtitleAudioDocument(
                samples,
                [new SubtitleSpeakerSegment(0, 0, duration.TotalSeconds)]);
        }

        if (lease is null) throw new InvalidOperationException("字幕说话人模型尚未准备完成。");
        _log.Info(
            $"{tag} stage=diarization status=queued 说话人分离开始：" +
            $"时长={duration.TotalSeconds:F2}s，线程={lease.Key.Threads}，阈值={configuration.ClusteringThreshold:F2}。");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var stableSegments = await lease.ProcessAsync(models =>
        {
            _log.Info($"{tag} stage=diarization-segment status=start waitMs={started.Elapsed.TotalMilliseconds:F0}");
            var inference = System.Diagnostics.Stopwatch.StartNew();
            var segments = models.Segment(samples, configuration.ClusteringThreshold);
            _log.Info($"{tag} stage=diarization-segment status=complete elapsedMs={inference.Elapsed.TotalMilliseconds:F0} parts={segments.Count}");
            // Native inference is synchronous. Wait for it before cancellation/disposal,
            // and never publish cancelled results or mutate the speaker registry with them.
            cancellationToken.ThrowIfCancellationRequested();
            _log.Info($"{tag} stage=speaker-embedding status=start");
            inference.Restart();
            var assigned = AssignStableSpeakers(samples, MergeAdjacent(segments), models, speakerRegistry, cancellationToken);
            _log.Info($"{tag} stage=speaker-embedding status=complete elapsedMs={inference.Elapsed.TotalMilliseconds:F0} speakers={assigned.Select(segment => segment.Speaker).Distinct().Count()}");
            return assigned;
        }, cancellationToken).ConfigureAwait(false);
        started.Stop();
        _log.Info(
            $"{tag} stage=diarization status=complete 说话人分离完成：耗时={started.Elapsed.TotalMilliseconds:F0}ms，" +
            $"分段={stableSegments.Count}，" +
            $"说话人={stableSegments.Select(segment => segment.Speaker).Distinct().Count()}，" +
            $"会话声纹={speakerRegistry.Count}。");
        return new SubtitleAudioDocument(samples, stableSegments);
    }

    public static string WriteSegmentToTemporaryWave(
        SubtitleAudioDocument document,
        SubtitleSpeakerSegment segment)
    {
        var start = Math.Clamp(
            (int)Math.Floor((segment.StartSeconds - 0.08) * SampleRate),
            0,
            document.Samples.Length);
        var end = Math.Clamp(
            (int)Math.Ceiling((segment.EndSeconds + 0.08) * SampleRate),
            start,
            document.Samples.Length);
        var path = ApplicationDataPaths.CreateTemporaryFilePath(
            "steamvr-translator-subtitle",
            ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        var bytes = new byte[Math.Min(8192, Math.Max(2, (end - start) * 2))];
        var sampleIndex = start;
        while (sampleIndex < end)
        {
            var count = Math.Min(bytes.Length / 2, end - sampleIndex);
            for (var i = 0; i < count; i++)
            {
                var sample = Math.Clamp(document.Samples[sampleIndex + i], -1f, 1f);
                var value = sample < 0
                    ? (short)(sample * -short.MinValue)
                    : (short)(sample * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), value);
            }

            writer.Write(bytes, 0, count * 2);
            sampleIndex += count;
        }

        return path;
    }

    private static float[] ReadMonoSamples(string audioPath, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(audioPath);
        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels > 1)
        {
            source = new ChannelAveragingSampleProvider(source);
        }

        if (source.WaveFormat.SampleRate != SampleRate)
        {
            source = new WdlResamplingSampleProvider(source, SampleRate);
        }

        var samples = new List<float>((int)Math.Min(int.MaxValue, reader.TotalTime.TotalSeconds * SampleRate));
        var buffer = new float[SampleRate];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return samples.ToArray();
    }

    private static IReadOnlyList<SubtitleSpeakerSegment> MergeAdjacent(
        IReadOnlyList<SubtitleSpeakerSegment> segments)
    {
        var merged = new List<SubtitleSpeakerSegment>();
        foreach (var segment in segments)
        {
            if (merged.Count > 0 &&
                merged[^1].Speaker == segment.Speaker &&
                segment.StartSeconds - merged[^1].EndSeconds <= 0.35)
            {
                merged[^1] = merged[^1] with
                {
                    EndSeconds = Math.Max(merged[^1].EndSeconds, segment.EndSeconds)
                };
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    private static IReadOnlyList<SubtitleSpeakerSegment> AssignStableSpeakers(
        float[] samples,
        IReadOnlyList<SubtitleSpeakerSegment> segments,
        ISubtitleDiarizationModels models,
        SpeakerIdentityRegistry registry,
        CancellationToken cancellationToken)
    {
        var stableIds = new Dictionary<int, int>();
        var embeddings = new Dictionary<int, float[]?>();
        foreach (var group in segments.GroupBy(segment => segment.Speaker))
        {
            cancellationToken.ThrowIfCancellationRequested();
            const int maximumSamples = SampleRate * 12;
            var speakerSamples = new List<float>(maximumSamples);
            foreach (var segment in group)
            {
                var start = Math.Clamp(
                    (int)Math.Floor(segment.StartSeconds * SampleRate),
                    0,
                    samples.Length);
                var end = Math.Clamp(
                    (int)Math.Ceiling(segment.EndSeconds * SampleRate),
                    start,
                    samples.Length);
                var count = Math.Min(end - start, maximumSamples - speakerSamples.Count);
                if (count > 0)
                {
                    speakerSamples.AddRange(samples.AsSpan(start, count).ToArray());
                }
                if (speakerSamples.Count >= maximumSamples)
                {
                    break;
                }
            }

            embeddings[group.Key] = models.Embed(speakerSamples.ToArray());
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (speaker, embedding) in embeddings)
            stableIds[speaker] = embedding is null ? registry.ReserveAnonymous() : registry.Resolve(embedding);

        return segments
            .Select(segment => segment with { Speaker = stableIds[segment.Speaker] })
            .ToArray();
    }

    private sealed class ChannelAveragingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        public ChannelAveragingSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requested = count * _channels;
            if (_sourceBuffer.Length < requested)
            {
                _sourceBuffer = new float[requested];
            }

            var sourceRead = _source.Read(_sourceBuffer, 0, requested);
            var frames = sourceRead / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sum = 0f;
                for (var channel = 0; channel < _channels; channel++)
                {
                    sum += _sourceBuffer[frame * _channels + channel];
                }

                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}

public sealed record SubtitleAudioDocument(
    float[] Samples,
    IReadOnlyList<SubtitleSpeakerSegment> Segments);

public sealed record SubtitleSpeakerSegment(
    int Speaker,
    double StartSeconds,
    double EndSeconds);
