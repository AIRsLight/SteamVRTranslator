namespace SteamVRTranslator.Core.Selection;

public enum SelectionTransitionKind
{
    None,
    Armed,
    SizingStarted,
    RegionUpdated,
    RegionLocked,
    RegionRejected,
    OrientationRejected,
    SubmissionRequested,
    Cancelled,
    Completed,
    TimedOut
}

public sealed record SelectionTransition(
    SelectionTransitionKind Kind,
    SelectionSnapshot Snapshot,
    NormalizedRect? SubmittedRegion = null);
