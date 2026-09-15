namespace SteamVRTranslator.App.SteamVR;

public sealed partial class SteamVrTranslationRuntime
{
    private long _overlayThemeRevision = -1;

    private void RefreshOverlayTheme()
    {
        var revision = OverlayTheme.Instance.Revision;
        if (_overlayThemeRevision == revision) return;
        _overlayThemeRevision = revision;
        foreach (var overlay in _interactiveOverlays)
        {
            overlay.IsDirty = overlay.IsChromeDirty = overlay.IsProgressDirty = true;
            overlay.RenderedPointerPressed = null;
            overlay.SynchronizeTextureBuffers = overlay.SynchronizeChromeTextureBuffers = true;
            overlay.SynchronizeProgressTextureBuffers = true;
            overlay.SynchronizePointerTextureBuffers = overlay.SynchronizeRayTextureBuffers = true;
            overlay.WindowSource?.SetInteractionHighlighted(overlay.IsHighlighted);
            overlay.WindowSource?.Invalidate();
        }
        _voiceEchoFrame = null;
        _voiceEchoRenderedRevision = 0;
        _lastRenderedSnapshot = null;
    }
}
