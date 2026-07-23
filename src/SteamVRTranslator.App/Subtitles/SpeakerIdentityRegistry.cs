namespace SteamVRTranslator.App.Subtitles;

public sealed class SpeakerIdentityRegistry
{
    private readonly List<SpeakerProfile> _profiles = [];
    private readonly double _matchThreshold;

    public SpeakerIdentityRegistry(double matchThreshold = 0.65)
    {
        if (matchThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(matchThreshold));
        }

        _matchThreshold = matchThreshold;
    }

    public int Count => _profiles.Count;

    public void Reset() => _profiles.Clear();

    public int ReserveAnonymous()
    {
        var id = _profiles.Count;
        _profiles.Add(new SpeakerProfile(id));
        return id;
    }

    public int Resolve(IReadOnlyList<float> embedding)
    {
        var normalized = Normalize(embedding);
        var bestIndex = -1;
        var bestScore = double.NegativeInfinity;
        for (var index = 0; index < _profiles.Count; index++)
        {
            var centroid = _profiles[index].Centroid;
            if (centroid is null)
            {
                continue;
            }

            var score = Cosine(centroid, normalized);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        if (bestIndex >= 0 && bestScore >= _matchThreshold)
        {
            var profile = _profiles[bestIndex];
            profile.Update(normalized);
            return profile.Id;
        }

        var id = _profiles.Count;
        _profiles.Add(new SpeakerProfile(id, normalized));
        return id;
    }

    internal static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count || left.Count == 0)
        {
            return double.NegativeInfinity;
        }

        double dot = 0;
        double leftLength = 0;
        double rightLength = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }

        return dot / Math.Sqrt(leftLength * rightLength);
    }

    private static float[] Normalize(IReadOnlyList<float> embedding)
    {
        if (embedding.Count == 0)
        {
            throw new ArgumentException("说话人嵌入不能为空。", nameof(embedding));
        }

        var length = Math.Sqrt(embedding.Sum(value => value * value));
        if (length <= 1e-8)
        {
            throw new ArgumentException("说话人嵌入不能是零向量。", nameof(embedding));
        }

        return embedding.Select(value => (float)(value / length)).ToArray();
    }

    private sealed class SpeakerProfile
    {
        public SpeakerProfile(int id)
        {
            Id = id;
        }

        public SpeakerProfile(int id, float[] centroid)
        {
            Id = id;
            Centroid = centroid;
        }

        public int Id { get; }

        public float[]? Centroid { get; private set; }

        public int ObservationCount { get; private set; } = 1;

        public void Update(float[] embedding)
        {
            var retainedObservations = Math.Min(ObservationCount, 8);
            if (Centroid is null)
            {
                Centroid = embedding;
                ObservationCount = 1;
                return;
            }

            var updated = new float[Centroid.Length];
            for (var index = 0; index < updated.Length; index++)
            {
                updated[index] =
                    (Centroid[index] * retainedObservations + embedding[index]) /
                    (retainedObservations + 1);
            }

            Centroid = Normalize(updated);
            ObservationCount++;
        }
    }
}
