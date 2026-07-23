using SteamVRTranslator.Core.Selection;
using Xunit;

namespace SteamVRTranslator.Core.Tests;

public sealed class SelectionStateMachineTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ToggleArmsThenSubmitsLockedRegion()
    {
        var machine = new SelectionStateMachine();

        Assert.Equal(SelectionTransitionKind.Armed, machine.Toggle(Start).Kind);
        machine.Update(true, true, new(0.2f, 0.3f), new(0.8f, 0.7f), Start.AddSeconds(1));
        var locked = machine.Update(true, false, new(0.2f, 0.3f), new(0.8f, 0.7f), Start.AddSeconds(2));
        var submitted = machine.Toggle(Start.AddSeconds(3));

        Assert.Equal(SelectionTransitionKind.RegionLocked, locked.Kind);
        Assert.Equal(SelectionTransitionKind.SubmissionRequested, submitted.Kind);
        Assert.Equal(new NormalizedRect(0.2f, 0.3f, 0.8f, 0.7f), submitted.SubmittedRegion);
    }

    [Fact]
    public void ReleasingEitherTriggerLocksLastCompleteRegion()
    {
        var machine = new SelectionStateMachine();
        machine.Toggle(Start);
        machine.Update(true, true, new(0.1f, 0.1f), new(0.6f, 0.6f), Start.AddSeconds(1));
        machine.Update(true, true, new(0.2f, 0.2f), new(0.9f, 0.8f), Start.AddSeconds(2));

        var transition = machine.Update(false, true, null, new(0.95f, 0.9f), Start.AddSeconds(3));

        Assert.Equal(SelectionState.Locked, transition.Snapshot.State);
        Assert.Equal(new NormalizedRect(0.2f, 0.2f, 0.9f, 0.8f), transition.Snapshot.Region);
    }

    [Fact]
    public void ToggleBeforeLockCancelsSelection()
    {
        var machine = new SelectionStateMachine();
        machine.Toggle(Start);

        var transition = machine.Toggle(Start.AddSeconds(1));

        Assert.Equal(SelectionTransitionKind.Cancelled, transition.Kind);
        Assert.Equal(SelectionState.Idle, transition.Snapshot.State);
    }

    [Fact]
    public void TinyRegionIsRejectedAndSelectionRemainsArmed()
    {
        var machine = new SelectionStateMachine();
        machine.Toggle(Start);
        machine.Update(true, true, new(0.5f, 0.5f), new(0.51f, 0.51f), Start.AddSeconds(1));
        var transition = machine.Update(false, true, null, null, Start.AddSeconds(2));

        Assert.Equal(SelectionTransitionKind.RegionRejected, transition.Kind);
        Assert.Equal(SelectionState.Armed, transition.Snapshot.State);
        Assert.Null(transition.Snapshot.Region);
    }

    [Fact]
    public void ActiveSelectionTimesOut()
    {
        var machine = new SelectionStateMachine(TimeSpan.FromSeconds(5));
        machine.Toggle(Start);

        var transition = machine.Tick(Start.AddSeconds(6));

        Assert.Equal(SelectionTransitionKind.TimedOut, transition.Kind);
        Assert.Equal(SelectionState.Idle, transition.Snapshot.State);
    }

    [Fact]
    public void InvalidFrameOrientationCannotBeLocked()
    {
        var machine = new SelectionStateMachine();
        machine.Toggle(Start);
        machine.Update(
            true,
            true,
            new(0.2f, 0.2f),
            new(0.8f, 0.8f),
            Start.AddSeconds(1),
            frameUsable: false);

        var transition = machine.Update(
            false,
            true,
            null,
            null,
            Start.AddSeconds(2),
            frameUsable: false);

        Assert.Equal(SelectionTransitionKind.OrientationRejected, transition.Kind);
        Assert.Equal(SelectionState.Armed, transition.Snapshot.State);
        Assert.Null(transition.Snapshot.Region);
        Assert.Null(transition.Snapshot.LeftPointer);
        Assert.Null(transition.Snapshot.RightPointer);
    }

    [Fact]
    public void MissingPointersBeforeReleaseCannotLockThePreviousFrame()
    {
        var machine = new SelectionStateMachine();
        machine.Toggle(Start);
        machine.Update(
            true,
            true,
            new(0.2f, 0.2f),
            new(0.8f, 0.8f),
            Start.AddSeconds(1));
        machine.Update(
            true,
            true,
            null,
            null,
            Start.AddSeconds(2),
            frameUsable: false);

        var transition = machine.Update(
            false,
            true,
            null,
            null,
            Start.AddSeconds(3),
            frameUsable: false);

        Assert.Equal(SelectionTransitionKind.OrientationRejected, transition.Kind);
        Assert.Equal(SelectionState.Armed, transition.Snapshot.State);
        Assert.Null(transition.Snapshot.Region);
    }
}
