using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

public sealed class SubtitleAudioProcessor
{
    public const int SampleRate = 16000;
    private readonly AppLog _log;

    public SubtitleAudioProcessor(AppLog log)
    {
        _log = log;
    }

    public async Task<SubtitleAudioDocument> AnalyzeAsync(
        string audioPath,
        SubtitleDiarizationConfiguration configuration,
        SpeakerIdentityRegistry speakerRegistry,
        CancellationToken cancellationToken)
    {
        var samples = await Task.Run(
            () => ReadMonoSamples(audioPath, cancellationToken),
            cancellationToken);
        if (samples.Length == 0)
        {
            throw new InvalidOperationException("音频文件没有可识别的采样数据。");
        }

        var duration = TimeSpan.FromSeconds((double)samples.Length / SampleRate);
        if (!configuration.Enabled)
        {
            return new SubtitleAudioDocument(
                samples,
                [new SubtitleSpeakerSegment(0, 0, duration.TotalSeconds)]);
        }

        var segmentationPath = ResolveRequiredPath(
            configuration.SegmentationModelPath,
            "Pyannote 说话人分段模型");
        var embeddingPath = ResolveRequiredPath(
            configuration.EmbeddingModelPath,
            "3D-Speaker 声纹模型");
        var threads = Math.Clamp(configuration.CpuThreadCount, 1, Math.Max(1, Environment.ProcessorCount));
        _log.Info(
            $"[subtitles] 说话人分离开始：音频={Path.GetFileName(audioPath)}，" +
            $"时长={duration.TotalSeconds:F2}s，线程={threads}，阈值={configuration.ClusteringThreshold:F2}。");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var segments = await Task.Run(() =>
        {
            var sherpaConfiguration = new OfflineSpeakerDiarizationConfig();
            sherpaConfiguration.Segmentation.Pyannote.Model = segmentationPath;
            sherpaConfiguration.Segmentation.NumThreads = threads;
            sherpaConfiguration.Embedding.Model = embeddingPath;
            sherpaConfiguration.Embedding.NumThreads = threads;
            sherpaConfiguration.Clustering.NumClusters = 0;
            sherpaConfiguration.Clustering.Threshold = (float)configuration.ClusteringThreshold;
            using var diarizer = new OfflineSpeakerDiarization(sherpaConfiguration);
            if (diarizer.SampleRate != SampleRate)
            {
                throw new InvalidOperationException(
                    $"说话人模型要求 {diarizer.SampleRate} Hz，当前处理链为 {SampleRate} Hz。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return diarizer.Process(samples)
                .Select(segment => new SubtitleSpeakerSegment(
                    segment.Speaker,
                    Math.Max(0, segment.Start),
                    Math.Min(duration.TotalSeconds, segment.End)))
                .Where(segment => segment.EndSeconds - segment.StartSeconds >= 0.2)
                .OrderBy(segment => segment.StartSeconds)
                .ToArray();
        }, cancellationToken);
        started.Stop();
        var merged = MergeAdjacent(segments);
        var stableSegments = AssignStableSpeakers(
            samples,
            merged,
            embeddingPath,
            threads,
            speakerRegistry);
        _log.Info(
            $"[subtitles] 说话人分离完成：耗时={started.Elapsed.TotalMilliseconds:F0}ms，" +
            $"原始分段={segments.Length}，合并后={merged.Count}，" +
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
        string embeddingPath,
        int threads,
        SpeakerIdentityRegistry registry)
    {
        var extractorConfiguration = new SpeakerEmbeddingExtractorConfig
        {
            Model = embeddingPath,
            NumThreads = threads
        };
        using var extractor = new SpeakerEmbeddingExtractor(extractorConfiguration);
        var stableIds = new Dictionary<int, int>();
        foreach (var group in segments.GroupBy(segment => segment.Speaker))
        {
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

            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(SampleRate, speakerSamples.ToArray());
            stream.InputFinished();
            stableIds[group.Key] = extractor.IsReady(stream)
                ? registry.Resolve(extractor.Compute(stream))
                : registry.ReserveAnonymous();
        }

        return segments
            .Select(segment => segment with { Speaker = stableIds[segment.Speaker] })
            .ToArray();
    }

    private static string ResolveRequiredPath(string path, string displayName)
    {
        var resolved = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"{displayName}未安装：{resolved}", resolved);
        }

        return resolved;
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
