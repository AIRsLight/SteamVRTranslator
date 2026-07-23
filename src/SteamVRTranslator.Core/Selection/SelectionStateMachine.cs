namespace SteamVRTranslator.Core.Selection;

public sealed class SelectionStateMachine
{
    private readonly TimeSpan _timeout;
    private SelectionSnapshot _snapshot;
    private bool _lastFrameUsable = true;

    public SelectionStateMachine(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _snapshot = Idle(DateTimeOffset.MinValue);
    }

    public SelectionSnapshot Snapshot => _snapshot;

    public SelectionTransition Toggle(DateTimeOffset now)
    {
        return _snapshot.State switch
        {
            SelectionState.Idle => Set(
                SelectionTransitionKind.Armed,
                new SelectionSnapshot(SelectionState.Armed, false, false, null, null, null, now)),
            SelectionState.Locked when _snapshot.Region is { } region && region.IsUsable() => Set(
                SelectionTransitionKind.SubmissionRequested,
                _snapshot with { State = SelectionState.Submitting, ChangedAt = now },
                region),
            SelectionState.Submitting => NoChange(),
            _ => Cancel(now)
        };
    }

    public SelectionTransition Update(
        bool leftPressed,
        bool rightPressed,
        NormalizedPoint? leftPointer,
        NormalizedPoint? rightPointer,
        DateTimeOffset now,
        bool frameUsable = true)
    {
        if (_snapshot.State is SelectionState.Idle or SelectionState.Submitting or SelectionState.Locked)
        {
            return NoChange();
        }

        var pointersAvailable = leftPointer.HasValue && rightPointer.HasValue;
        if (_snapshot.State == SelectionState.Armed)
        {
            _snapshot = _snapshot with
            {
                LeftTriggerPressed = leftPressed,
                RightTriggerPressed = rightPressed,
                LeftPointer = leftPointer,
                RightPointer = rightPointer
            };

            if (leftPressed && rightPressed && pointersAvailable)
            {
                _lastFrameUsable = frameUsable;
                var region = NormalizedRect.FromCorners(leftPointer!.Value, rightPointer!.Value);
                return Set(
                    SelectionTransitionKind.SizingStarted,
                    _snapshot with { State = SelectionState.Sizing, Region = region, ChangedAt = now });
            }

            return NoChange();
        }

        if (leftPressed && rightPressed)
        {
            if (!pointersAvailable)
            {
                _lastFrameUsable = false;
                return NoChange();
            }

            _lastFrameUsable = frameUsable;
            var region = NormalizedRect.FromCorners(leftPointer!.Value, rightPointer!.Value);
            return Set(
                SelectionTransitionKind.RegionUpdated,
                _snapshot with
                {
                    LeftTriggerPressed = true,
                    RightTriggerPressed = true,
                    LeftPointer = leftPointer,
                    RightPointer = rightPointer,
                    Region = region
                });
        }

        if (_snapshot.Region is not { } lockedRegion || !lockedRegion.IsUsable())
        {
            return Set(
                SelectionTransitionKind.RegionRejected,
                new SelectionSnapshot(
                    SelectionState.Armed,
                    leftPressed,
                    rightPressed,
                    leftPointer,
                    rightPointer,
                    null,
                    now));
        }

        if (!_lastFrameUsable)
        {
            return Set(
                SelectionTransitionKind.OrientationRejected,
                _snapshot with
                {
                    State = SelectionState.Armed,
                    LeftTriggerPressed = leftPressed,
                    RightTriggerPressed = rightPressed,
                    LeftPointer = null,
                    RightPointer = null,
                    Region = null,
                    ChangedAt = now
                });
        }

        return Set(
            SelectionTransitionKind.RegionLocked,
            _snapshot with
            {
                State = SelectionState.Locked,
                LeftTriggerPressed = leftPressed,
                RightTriggerPressed = rightPressed,
                ChangedAt = now,
                Region = lockedRegion
            });
    }

    public SelectionTransition Tick(DateTimeOffset now)
    {
        if (_snapshot.State is SelectionState.Idle or SelectionState.Submitting)
        {
            return NoChange();
        }

        if (now - _snapshot.ChangedAt < _timeout)
        {
            return NoChange();
        }

        return Set(SelectionTransitionKind.TimedOut, Idle(now));
    }

    public SelectionTransition Complete(DateTimeOffset now) =>
        Set(SelectionTransitionKind.Completed, Idle(now));

    public SelectionTransition Cancel(DateTimeOffset now) =>
        Set(SelectionTransitionKind.Cancelled, Idle(now));

    private SelectionTransition NoChange() =>
        new(SelectionTransitionKind.None, _snapshot);

    private SelectionTransition Set(
        SelectionTransitionKind kind,
        SelectionSnapshot snapshot,
        NormalizedRect? submittedRegion = null)
    {
        _snapshot = snapshot;
        return new SelectionTransition(kind, snapshot, submittedRegion);
    }

    private static SelectionSnapshot Idle(DateTimeOffset now) =>
        new(SelectionState.Idle, false, false, null, null, null, now);
}
