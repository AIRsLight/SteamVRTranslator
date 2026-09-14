using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Diagnostics;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Output;

/// <summary>Decoded, immutable stereo audio shared by independent playback cursors.</summary>
internal sealed class VoiceInputCueClip(float[] samples)
{
    public int FrameCount => samples.Length / 2;
    public float Sample(int frame, int channel) => samples[frame * 2 + channel];
}

internal sealed record VoiceInputCueSounds(VoiceInputCueClip? Start = null,
    VoiceInputCueClip? Ongoing = null, VoiceInputCueClip? End = null)
{
    public static VoiceInputCueSounds Load(VoiceInputCueConfiguration configuration,
        string? baseDirectory = null, AppLog? fallbackLog = null)
    {
        return new(LoadStage(configuration.StartEnabled, configuration.StartFilePath),
            LoadStage(configuration.OngoingEnabled, configuration.OngoingFilePath),
            LoadStage(configuration.EndEnabled, configuration.EndFilePath));

        VoiceInputCueClip? LoadStage(bool enabled, string? path)
        {
            if (!enabled || string.IsNullOrWhiteSpace(path)) return null;
            try { return VoiceInputCueAudio.Load(Path.GetFullPath(path, baseDirectory ?? AppContext.BaseDirectory)); }
            catch (Exception exception) when (fallbackLog is not null)
            {
                fallbackLog.Warning($"[voice-cues] 自定义音效无法读取，使用该阶段的内置音效：{path}；{exception.Message}");
                return null;
            }
        }
    }
}

internal static class VoiceInputCueAudio
{
    internal const int MaximumSeconds = 30;
    internal const long MaximumFileBytes = 20 * 1024 * 1024;
    private const int MaximumSamples = VoiceInputCueSampleProvider.SampleRate * 2 * MaximumSeconds;

    public static VoiceInputCueClip Load(string path) => new(Decode(path));

    // Decode before creating a managed file; a failed import leaves the previous selection intact.
    public static string Import(string sourcePath, string? baseDirectory = null)
    {
        var samples = Decode(sourcePath);
        var root = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        var relativeDirectory = Path.Combine("sounds", "voice-cues", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, relativeDirectory);
        var fileName = Path.GetFileNameWithoutExtension(sourcePath) + ".wav";
        var destination = Path.Combine(directory, fileName);
        Directory.CreateDirectory(directory);
        try
        {
            using var writer = new WaveFileWriter(destination,
                WaveFormat.CreateIeeeFloatWaveFormat(VoiceInputCueSampleProvider.SampleRate, 2));
            writer.WriteSamples(samples, 0, samples.Length);
        }
        catch
        {
            File.Delete(destination);
            Directory.Delete(directory);
            throw;
        }
        return Path.Combine(relativeDirectory, fileName);
    }

    private static float[] Decode(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(AppLocalization.Text("Voice.Cues.File.Format"));
            if (new FileInfo(path).Length > MaximumFileBytes)
                throw new InvalidDataException(AppLocalization.Text("Voice.Cues.File.Limit"));
            using var reader = new AudioFileReader(path);
            if (reader.WaveFormat.Channels is not (1 or 2) || reader.WaveFormat.SampleRate is < 8000 or > 192000)
                throw new InvalidDataException(AppLocalization.Text("Voice.Cues.File.Format"));
            ISampleProvider source = reader;
            if (source.WaveFormat.SampleRate != VoiceInputCueSampleProvider.SampleRate)
                source = new WdlResamplingSampleProvider(source, VoiceInputCueSampleProvider.SampleRate);
            if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
            var samples = new List<float>();
            var buffer = new float[8192];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (samples.Count + count > MaximumSamples)
                    throw new InvalidDataException(AppLocalization.Text("Voice.Cues.File.Limit"));
                for (var i = 0; i < count; i++)
                {
                    if (!float.IsFinite(buffer[i]))
                        throw new InvalidDataException(AppLocalization.Text("Voice.Cues.File.Format"));
                    samples.Add(Math.Clamp(buffer[i], -1f, 1f));
                }
            }
            if (samples.Count < 2 || samples.Count % 2 != 0)
                throw new InvalidDataException(AppLocalization.Text("Voice.Cues.File.Format"));
            return samples.ToArray();
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(AppLocalization.Format("Voice.Cues.File.Failed",
                Path.GetFileName(path), exception.Message), exception);
        }
    }
}
