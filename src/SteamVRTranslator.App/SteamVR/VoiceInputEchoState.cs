using SteamVRTranslator.App.Output;

namespace SteamVRTranslator.App.SteamVR;

internal sealed record VoiceInputEchoSnapshot(long Revision, string StatusKey, string Text,
    int ChunkNumber = 0, int ChunkCount = 0, DateTimeOffset? ExpiresAt = null);

/// <summary>Async speech callbacks publish data; only the runtime loop touches OpenVR.</summary>
internal sealed class VoiceInputEchoState
{
    internal static readonly TimeSpan DisplayDuration = TimeSpan.FromSeconds(12);
    private readonly object _sync = new();
    private bool _enabled;
    private long _request, _revision;
    private VoiceInputEchoSnapshot? _snapshot;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            ResetCore();
        }
    }

    public long Begin()
    {
        lock (_sync)
        {
            ResetCore();
            if (_enabled) _snapshot = new(++_revision, "Voice.Echo.Recording", string.Empty);
            return _request;
        }
    }

    public void Update(long request, string statusKey, string? text = null)
    {
        lock (_sync)
        {
            if (!_enabled || request != _request || _snapshot is null) return;
            _snapshot = _snapshot with { Revision = ++_revision, StatusKey = statusKey, Text = text ?? _snapshot.Text };
        }
    }

    public void Sent(long request, OscChatboxEmission emission)
    {
        lock (_sync)
        {
            if (!_enabled || request != _request || _snapshot is null) return;
            _snapshot = new(++_revision, emission.IsUpdate ? "Voice.Echo.Updated" :
                emission.SendImmediately ? "Voice.Echo.Sent" : "Voice.Echo.Preview",
                emission.Text, emission.ChunkNumber, emission.ChunkCount);
        }
    }

    public void Complete(long request, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_enabled || request != _request || _snapshot is null) return;
            _snapshot = _snapshot with { Revision = ++_revision, ExpiresAt = now + DisplayDuration };
        }
    }

    public VoiceInputEchoSnapshot? Snapshot(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_snapshot?.ExpiresAt is { } expires && now >= expires) _snapshot = null;
            return _snapshot;
        }
    }

    public void Reset() { lock (_sync) ResetCore(); }
    private void ResetCore() { _request++; _revision++; _snapshot = null; }
}
