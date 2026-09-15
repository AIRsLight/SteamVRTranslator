using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

public sealed partial class SteamVrTranslationRuntime
{
    private readonly VoiceInputEchoState _voiceEcho = new();
    private readonly VoiceInputEchoPlacement _voiceEchoPlacement = new();
    private long _voiceEchoRequest;
    private ulong _voiceEchoOverlayHandle = OpenVR.k_ulOverlayHandleInvalid;
    private D3D11OverlayTexture? _voiceEchoTexture;
    private long _voiceEchoRenderedRevision;
    private long _voiceEchoPreparedRevision;
    private OverlayRenderFrame? _voiceEchoFrame;
    private ETrackingUniverseOrigin _voiceEchoTrackingOrigin;
    private bool _voiceEchoShown, _voiceEchoRenderFailed;

    public void ApplyVoiceTextEchoSetting(bool enabled)
    {
        _configuration.VrChatVoiceInput.TextEchoEnabled = enabled;
        _voiceEcho.SetEnabled(enabled && _configuration.VrChatVoiceInput.Enabled);
        var window = _controlPanelWindow;
        if (window is not null)
        {
            var state = CurrentControlPanelState();
            _ = window.Dispatcher.BeginInvoke(() => window.ApplyState(state));
        }
        _log.Info($"[voice-echo] 面前文本回显：enabled={enabled}。");
    }

    private void RenderVoiceEcho()
    {
        var snapshot = _voiceEcho.Snapshot(DateTimeOffset.UtcNow);
        // A new placement is allowed only after the previous display has actually ended.
        // Starting another utterance while visible retains the same world-space anchor.
        if (snapshot is null)
        {
            _voiceEchoPlacement.Reset();
            _voiceEchoFrame = null;
        }
        try
        {
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Text) ||
                !_spatialOverlaysVisible || _interactiveOverlaysSuppressed || _voiceEchoRenderFailed)
            {
                if (_voiceEchoShown)
                {
                    EnsureOverlay(OpenVR.Overlay.HideOverlay(_voiceEchoOverlayHandle), "隐藏语音文本回显");
                    _voiceEchoShown = false;
                }
                return;
            }
            if (_voiceEchoOverlayHandle == OpenVR.k_ulOverlayHandleInvalid)
            {
                // This handle lives until disconnect/shutdown. New PTT requests and expiry
                // only replace its texture or hide it, so repeated input never stacks windows.
                CreateOverlay("io.steamvrtranslator.voice-echo", "SteamVR Translator Voice Echo", VoiceInputEchoPlacement.WidthMeters, 110,
                    ref _voiceEchoOverlayHandle);
                ConfigureVoiceEchoDisplayOnly();
                // Keep this raw handle outside _interactiveOverlays: it must never enter
                // pointer hit testing, contact highlighting, scrolling or controller grabs.
                _log.Info("[voice-echo] 已创建语音文本回显浮窗：仅显示，不接收输入，不可拖动，固定在空间中。");
            }
            if (_voiceEchoFrame is null || _voiceEchoPreparedRevision != snapshot.Revision)
            {
                _voiceEchoFrame = _renderer.RenderVoiceEcho(snapshot);
                _voiceEchoPreparedRevision = snapshot.Revision;
            }
            var frame = _voiceEchoFrame;
            // Constant meters per pixel keeps the large type the same physical size as the box grows.
            var metersPerPixel = VoiceInputEchoPlacement.WidthMeters / OverlayRenderer.VoiceEchoWidth;
            var widthMeters = frame.PixelWidth * metersPerPixel;
            var heightMeters = frame.PixelHeight * metersPerPixel;
            if (!_voiceEchoPlacement.Transform.HasValue)
            {
                var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
                var origin = CurrentTrackingOrigin();
                OpenVR.System.GetDeviceToAbsoluteTrackingPose(origin, 0, poses);
                if (!_voiceEchoPlacement.TryPlace(poses[OpenVR.k_unTrackedDeviceIndex_Hmd], widthMeters, heightMeters)) return;
                _voiceEchoTrackingOrigin = origin;
                _log.Info($"[voice-echo] 已固定空间位置：origin={origin}。");
            }
            if (_voiceEchoRenderedRevision != snapshot.Revision)
            {
                if (_voiceEchoTexture is null || _voiceEchoTexture.Width != frame.PixelWidth || _voiceEchoTexture.Height != frame.PixelHeight)
                {
                    // Resize the texture on the existing overlay handle; never create another window.
                    EnsureOverlay(OpenVR.Overlay.ClearOverlayTexture(_voiceEchoOverlayHandle), "清理语音回显旧纹理");
                    _voiceEchoTexture?.Dispose();
                    _voiceEchoTexture = new D3D11OverlayTexture(_overlayTextureDevice!, frame.PixelWidth, frame.PixelHeight);
                    _voiceEchoTexture.Attach(_voiceEchoOverlayHandle);
                }
                var transform = _voiceEchoPlacement.TransformForSize(widthMeters, heightMeters);
                EnsureOverlay(OpenVR.Overlay.SetOverlayWidthInMeters(_voiceEchoOverlayHandle, widthMeters), "调整语音回显宽度");
                EnsureOverlay(OpenVR.Overlay.SetOverlayTransformAbsolute(_voiceEchoOverlayHandle, _voiceEchoTrackingOrigin, ref transform),
                    "固定语音回显空间位置");
                _voiceEchoTexture.Reset(frame.Pixels);
                _voiceEchoRenderedRevision = snapshot.Revision;
                _log.Info($"[voice-echo] 已更新浮窗：state={snapshot.StatusKey} chars={snapshot.Text.Length} chunk={snapshot.ChunkNumber}/{snapshot.ChunkCount} size={frame.PixelWidth}x{frame.PixelHeight} width={widthMeters:F2}m height={heightMeters:F2}m。");
            }
            if (!_voiceEchoShown)
            {
                EnsureOverlay(OpenVR.Overlay.ShowOverlay(_voiceEchoOverlayHandle), "显示语音文本回显");
                _voiceEchoShown = true;
            }
        }
        catch (Exception exception)
        {
            // A display failure must not interrupt recording, OSC or other overlays.
            _voiceEchoRenderFailed = true;
            if (_voiceEchoOverlayHandle != OpenVR.k_ulOverlayHandleInvalid)
                OpenVR.Overlay?.HideOverlay(_voiceEchoOverlayHandle);
            _voiceEchoShown = false;
            _log.Error("[voice-echo] 文本回显绘制失败。", exception);
        }
    }

    private void ConfigureVoiceEchoDisplayOnly()
    {
        var overlay = OpenVR.Overlay;
        EnsureOverlay(overlay.SetOverlayInputMethod(_voiceEchoOverlayHandle, VROverlayInputMethod.None),
            "禁用语音回显输入");
        foreach (var flag in new[]
        {
            VROverlayFlags.SendVRDiscreteScrollEvents,
            VROverlayFlags.SendVRSmoothScrollEvents,
            VROverlayFlags.SendVRTouchpadEvents,
            VROverlayFlags.MakeOverlaysInteractiveIfVisible,
            VROverlayFlags.WantsModalBehavior,
            VROverlayFlags.EnableControlBar,
            VROverlayFlags.EnableControlBarKeyboard,
            VROverlayFlags.EnableControlBarClose
        })
            EnsureOverlay(overlay.SetOverlayFlag(_voiceEchoOverlayHandle, flag, false), $"禁用语音回显交互：{flag}");
        EnsureOverlay(overlay.SetOverlayFlag(_voiceEchoOverlayHandle, VROverlayFlags.HideLaserIntersection, true),
            "隐藏语音回显射线交点");
    }
}
