namespace SteamVRTranslator.App.Configuration;

public sealed class VoiceInputCueConfiguration
{
    public const int DefaultVolumePercent = 35;

    public bool Enabled { get; set; }

    public bool EchoEnabled { get; set; }

    public bool StartEnabled { get; set; } = true;

    public bool OngoingEnabled { get; set; } = true;

    public bool EndEnabled { get; set; } = true;

    public string? StartFilePath { get; set; }

    public string? OngoingFilePath { get; set; }

    public string? EndFilePath { get; set; }

    public int VolumePercent { get; set; } = DefaultVolumePercent;

    public void Normalize()
    {
        VolumePercent = Math.Clamp(VolumePercent, 0, 100);
        StartFilePath = NormalizePath(StartFilePath);
        OngoingFilePath = NormalizePath(OngoingFilePath);
        EndFilePath = NormalizePath(EndFilePath);
    }

    private static string? NormalizePath(string? path) => string.IsNullOrWhiteSpace(path) ? null : path.Trim();

    public VoiceInputCueConfiguration Clone() => (VoiceInputCueConfiguration)MemberwiseClone();
}
