namespace SteamVRTranslator.App.Interaction;

public sealed class CommandPressTracker
{
    private readonly TimeSpan _holdThreshold;
    private DateTimeOffset? _pressedAt;
    private bool _longHoldActivated;

    public CommandPressTracker(TimeSpan holdThreshold)
    {
        if (holdThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdThreshold));
        }

        _holdThreshold = holdThreshold;
    }

    public bool IsPressed => _pressedAt.HasValue;

    public TimeSpan HeldDuration(DateTimeOffset now) =>
        _pressedAt is { } pressedAt ? now - pressedAt : TimeSpan.Zero;

    public void Press(DateTimeOffset now)
    {
        if (_pressedAt.HasValue)
        {
            return;
        }

        _pressedAt = now;
        _longHoldActivated = false;
    }

    public bool ActivateLongHold(DateTimeOffset now)
    {
        if (_longHoldActivated || HeldDuration(now) < _holdThreshold)
        {
            return false;
        }

        _longHoldActivated = true;
        return true;
    }

    public CommandPressKind Release(DateTimeOffset now)
    {
        if (_pressedAt is null)
        {
            return CommandPressKind.None;
        }

        var kind = _longHoldActivated || HeldDuration(now) >= _holdThreshold
            ? CommandPressKind.CustomCommand
            : CommandPressKind.Translate;
        Reset();
        return kind;
    }

    public void Reset()
    {
        _pressedAt = null;
        _longHoldActivated = false;
    }
}

public enum CommandPressKind
{
    None,
    Translate,
    CustomCommand
}
