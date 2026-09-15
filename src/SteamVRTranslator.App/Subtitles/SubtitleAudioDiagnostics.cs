using SteamVRTranslator.App.Diagnostics;

namespace SteamVRTranslator.App.Subtitles;

/// <summary>Capture-thread counters. Emits a first-packet record and at most one heartbeat per five seconds.</summary>
internal sealed class SubtitleAudioDiagnostics(AppLog log, string tag)
{
    internal const double VoiceThreshold = 0.0025;
    private long _packets, _frames, _silentPackets, _audiblePackets, _discontinuities, _timestampErrors;
    private long _windowSamples;
    private double _windowSquares, _peak;
    private TimeSpan _lastReport, _lastPacket;
    private bool _firstReported;

    public double Record(ReadOnlySpan<byte> pcm, uint frames, uint flags, TimeSpan elapsed)
    {
        var (rms, peak) = Level(pcm);
        _packets++; _frames += frames; _lastPacket = elapsed;
        if ((flags & 2) != 0) _silentPackets++;
        if ((flags & 1) != 0) _discontinuities++;
        if ((flags & 4) != 0) _timestampErrors++;
        if (rms >= VoiceThreshold) _audiblePackets++;
        var samples = pcm.Length / 2;
        _windowSamples += samples;
        _windowSquares += rms * rms * samples;
        _peak = Math.Max(_peak, peak);
        return rms;
    }

    public void Report(TimeSpan elapsed, bool final = false)
    {
        var first = !_firstReported && _packets > 0;
        if (!final && !first && elapsed - _lastReport < TimeSpan.FromSeconds(5)) return;
        var rms = _windowSamples == 0 ? 0 : Math.Sqrt(_windowSquares / _windowSamples);
        var gap = _packets == 0 ? "none" : (elapsed - _lastPacket).TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        log.Info(FormattableString.Invariant($"{tag} stage=audio status={(final ? "final" : first ? "first-packet" : "heartbeat")} elapsedMs={elapsed.TotalMilliseconds:F0} packets={_packets} frames={_frames} silentPackets={_silentPackets} audiblePackets={_audiblePackets} discontinuities={_discontinuities} timestampErrors={_timestampErrors} windowSamples={_windowSamples} rms={rms:F6} peak={_peak:F6} voiceThreshold={VoiceThreshold:F4} lastPacketAgoMs={gap}"));
        _firstReported |= first;
        _lastReport = elapsed;
        _windowSamples = 0; _windowSquares = 0; _peak = 0;
    }

    internal static (double Rms, double Peak) Level(ReadOnlySpan<byte> pcm)
    {
        double squares = 0, peak = 0;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm[index..]) / 32768d;
            squares += value * value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        return (pcm.Length < 2 ? 0 : Math.Sqrt(squares / (pcm.Length / 2)), peak);
    }
}
