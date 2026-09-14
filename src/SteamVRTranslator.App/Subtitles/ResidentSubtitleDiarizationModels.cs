using SherpaOnnx;

namespace SteamVRTranslator.App.Subtitles;

internal sealed class ResidentSubtitleDiarizationModels : ISubtitleDiarizationModels
{
    private readonly OfflineSpeakerDiarization _diarizer;
    private readonly SpeakerEmbeddingExtractor _extractor;
    private OfflineSpeakerDiarizationConfig _configuration;
    private bool _disposed;

    public ResidentSubtitleDiarizationModels(SubtitleDiarizationModelKey key, CancellationToken token)
    {
        RequireModel(key.SegmentationPath, "Pyannote 说话人分段模型");
        RequireModel(key.EmbeddingPath, "3D-Speaker 声纹模型");
        token.ThrowIfCancellationRequested();
        _configuration = new OfflineSpeakerDiarizationConfig();
        _configuration.Segmentation.Pyannote.Model = key.SegmentationPath;
        _configuration.Segmentation.NumThreads = key.Threads;
        _configuration.Embedding.Model = key.EmbeddingPath;
        _configuration.Embedding.NumThreads = key.Threads;
        _configuration.Clustering.NumClusters = 0;
        _configuration.Clustering.Threshold = .9f;
        _diarizer = new OfflineSpeakerDiarization(_configuration);
        SpeakerEmbeddingExtractor? extractor = null;
        try
        {
            if (_diarizer.SampleRate != SubtitleAudioProcessor.SampleRate)
                throw new InvalidOperationException($"说话人模型要求 {_diarizer.SampleRate} Hz，当前处理链为 {SubtitleAudioProcessor.SampleRate} Hz。");
            token.ThrowIfCancellationRequested();
            extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
            {
                Model = key.EmbeddingPath, NumThreads = key.Threads
            });
            token.ThrowIfCancellationRequested();
            _extractor = extractor;
        }
        catch
        {
            try { extractor?.Dispose(); }
            finally { _diarizer.Dispose(); }
            throw;
        }
    }

    public IReadOnlyList<SubtitleSpeakerSegment> Segment(float[] samples, double threshold)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // sherpa-onnx SetConfig updates clustering without reloading the ONNX models.
        if (_configuration.Clustering.Threshold != (float)threshold)
        {
            _configuration.Clustering.Threshold = (float)threshold;
            _diarizer.SetConfig(_configuration);
        }
        var duration = (double)samples.Length / SubtitleAudioProcessor.SampleRate;
        return _diarizer.Process(samples)
            .Select(segment => new SubtitleSpeakerSegment(segment.Speaker, Math.Max(0, segment.Start), Math.Min(duration, segment.End)))
            .Where(segment => segment.EndSeconds - segment.StartSeconds >= .2)
            .OrderBy(segment => segment.StartSeconds).ToArray();
    }

    public float[]? Embed(float[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(SubtitleAudioProcessor.SampleRate, samples);
        stream.InputFinished();
        return _extractor.IsReady(stream) ? _extractor.Compute(stream) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _extractor.Dispose(); }
        finally { _diarizer.Dispose(); }
    }

    private static void RequireModel(string path, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{name}未安装：{path}", path);
    }
}
