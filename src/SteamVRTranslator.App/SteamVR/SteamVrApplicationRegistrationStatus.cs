using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

internal readonly record struct SteamVrApplicationRegistrationStatus(
    EVRApplicationError ManifestError,
    EVRApplicationError IdentityError)
{
    public bool ManifestAccepted =>
        ManifestError is EVRApplicationError.None or EVRApplicationError.AppKeyAlreadyExists;

    // A late-started client can load its action manifest directly even when SteamVR's
    // application index has not incorporated a newly registered manifest yet.
    public bool CanContinue => ManifestAccepted;
}
