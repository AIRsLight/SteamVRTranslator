using NAudio.Wave;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Output;

/// <summary>Sequences custom or built-in cues, looping the ongoing stage until PTT release.</summary>
internal sealed class VoiceInputCueSampleProvider : ISampleProvider
{
    internal const int SampleRate = 48000;
    internal const int OpeningMilliseconds = 180;
    internal const int ClosingMilliseconds = 220;
    private const int FadeMilliseconds = 10;
    private static readonly double NoiseHighPassCoefficient = Math.Exp(-2 * Math.PI * 45 / SampleRate);
    private static readonly double NoiseLowPassCoefficient = 1 - Math.Exp(-2 * Math.PI * 650 / SampleRate);
    private readonly object _sync = new();
    private readonly VoiceInputCueConfiguration _configuration;
    private long _frame;
    private bool _ending;
    private (float Left, float Right) _lastSample;
    private (float Left, float Right) _releaseSample;
    private uint _noiseState = 0x6D2B79F5;
    private double _lowPass;
    private double _secondLowPass;
    private double _highPass;
    private double _previousNoise;

    public VoiceInputCueSampleProvider(VoiceInputCueConfiguration configuration, VoiceInputCueSounds? sounds = null)
    {
        _configuration = configuration.Clone();
        _configuration.Normalize();
        Sounds = sounds ?? VoiceInputCueSounds.Load(_configuration);
    }

    internal VoiceInputCueSounds Sounds { get; }
    private int OpeningFrames => !_configuration.StartEnabled ? 0 : Sounds.Start?.FrameCount ?? Frames(OpeningMilliseconds);
    private int ClosingFrames => !_configuration.EndEnabled ? 0 : Sounds.End?.FrameCount ?? Frames(ClosingMilliseconds);
    internal TimeSpan OpeningDuration => TimeSpan.FromSeconds((double)OpeningFrames / SampleRate);
    internal TimeSpan OngoingPreviewDuration => TimeSpan.FromSeconds(!_configuration.OngoingEnabled ? 0.25 :
        Math.Max(2, (double)(Sounds.Ongoing?.FrameCount ?? 0) / SampleRate));
    internal TimeSpan EndingTimeout => TimeSpan.FromSeconds((double)ClosingFrames / SampleRate + 3);

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    public void End()
    {
        lock (_sync)
        {
            if (_ending)
            {
                return;
            }

            // Discard the recording sequence immediately, with a short fade to avoid a click.
            _ending = true;
            _frame = 0;
            _releaseSample = _lastSample;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            var written = 0;
            var endFrames = Frames(FadeMilliseconds) + ClosingFrames;
            while (written + 1 < count)
            {
                if (_ending && _frame >= endFrames)
                {
                    break;
                }

                var sample = _ending ? ReadEndingFrame() : ReadRecordingFrame();
                buffer[offset + written++] = sample.Left;
                buffer[offset + written++] = sample.Right;
                _lastSample = sample;
                _frame++;
            }

            return written;
        }
    }

    private (float Left, float Right) ReadRecordingFrame()
    {
        var openingFrames = OpeningFrames;
        if (_frame < openingFrames)
        {
            return Sounds.Start is { } start ? CustomFrame(start, (int)_frame) : Mono(NotificationChime(_frame, opening: true));
        }

        if (_configuration.OngoingEnabled && Sounds.Ongoing is { } ongoing)
            return CustomFrame(ongoing, (int)((_frame - openingFrames) % ongoing.FrameCount));
        return Mono(_configuration.OngoingEnabled
            ? (float)(RadioNoise() * 0.22 * _configuration.VolumePercent / 100 *
                Math.Min(1, (double)(_frame - openingFrames) / Frames(FadeMilliseconds)))
            : 0);
    }

    private (float Left, float Right) ReadEndingFrame()
    {
        var fadeFrames = Frames(FadeMilliseconds);
        if (_frame < fadeFrames)
        {
            var gain = 1 - (float)(_frame + 1) / fadeFrames;
            return (_releaseSample.Left * gain, _releaseSample.Right * gain);
        }

        if (!_configuration.EndEnabled) return (0, 0);
        return Sounds.End is { } end ? CustomFrame(end, (int)(_frame - fadeFrames)) :
            Mono(NotificationChime(_frame - fadeFrames, opening: false));
    }

    private (float Left, float Right) CustomFrame(VoiceInputCueClip clip, int frame)
    {
        // Fade file edges to reduce clicks when looping or switching stages.
        var fade = Math.Min(Frames(FadeMilliseconds), Math.Max(1, clip.FrameCount / 2));
        var gain = _configuration.VolumePercent / 100f * Math.Min(1f,
            Math.Min((float)frame / fade, (float)(clip.FrameCount - 1 - frame) / fade));
        return (clip.Sample(frame, 0) * gain, clip.Sample(frame, 1) * gain);
    }

    private static (float Left, float Right) Mono(float sample) => (sample, sample);

    private float NotificationChime(long frame, bool opening)
    {
        var length = Frames(opening ? OpeningMilliseconds : ClosingMilliseconds);
        var time = (double)frame / SampleRate;
        // Bright, clean two-note chimes: rising on press and falling on release.
        // The brief attack avoids a hard click; the second harmonic adds clarity.
        const double lowNote = 1046.50;
        const double highNote = 1567.98;
        var tone = NotificationNote(time, opening ? lowNote : highNote) +
            NotificationNote(time - (opening ? 0.065 : 0.085), opening ? highNote : lowNote);
        var tail = Math.Min(1, (double)(length - 1 - frame) / Frames(FadeMilliseconds));
        return (float)(tone * 0.45 * tail * _configuration.VolumePercent / 100);
    }

    private static double NotificationNote(double time, double frequency)
    {
        if (time < 0) return 0;
        var phase = 2 * Math.PI * frequency * time;
        var envelope = Math.Min(1, time / 0.003) * Math.Exp(-time * 30);
        return (Math.Sin(phase) + 0.16 * Math.Sin(2 * phase)) * envelope;
    }

    private double RadioNoise()
    {
        // Low-frequency noise bed: remove sub-bass/DC and use two low-pass stages
        // to soften high-frequency hiss without adding a resonant tone.
        _noiseState ^= _noiseState << 13;
        _noiseState ^= _noiseState >> 17;
        _noiseState ^= _noiseState << 5;
        var white = _noiseState / (double)uint.MaxValue * 2 - 1;
        _highPass = NoiseHighPassCoefficient * (_highPass + white - _previousNoise);
        _previousNoise = white;
        _lowPass += NoiseLowPassCoefficient * (_highPass - _lowPass);
        _secondLowPass += NoiseLowPassCoefficient * (_lowPass - _secondLowPass);
        return _secondLowPass;
    }

    private static int Frames(int milliseconds) => SampleRate * milliseconds / 1000;
}
