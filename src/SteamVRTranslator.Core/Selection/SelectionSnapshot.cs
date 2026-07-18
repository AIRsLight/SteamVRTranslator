namespace SteamVRTranslator.Core.Selection;

public sealed record SelectionSnapshot(
    SelectionState State,
    bool LeftTriggerPressed,
    bool RightTriggerPressed,
    NormalizedPoint? LeftPointer,
    NormalizedPoint? RightPointer,
    NormalizedRect? Region,
    DateTimeOffset ChangedAt);

