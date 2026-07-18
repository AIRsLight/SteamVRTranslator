# Architecture

## Interaction state machine

```text
Idle
  -> translation key
Armed
  -> both triggers held with valid controller positions
Sizing
  -> either trigger released
Locked
  -> translation key down begins temporary audio capture
  -> release before hold threshold: discard audio and translate
  -> release after hold threshold: SenseVoice transcription and custom command
Submitting
  -> backend completion or failure
Idle
```

Pressing the translation key before a region is locked cancels the operation. Active selection also has a configurable timeout.

## Coordinate contract

The two controller positions are opposite corners of a spatial rectangle. Two points alone do not uniquely define a plane, so the implementation chooses the plane that:

- contains both controller positions;
- faces the HMD as directly as the first constraint allows;
- keeps its up axis as close to tracking-space vertical as possible.

The overlay is transformed into the compositor's current tracking space with `SetOverlayTransformAbsolute`. Each corner is projected through the configured eye matrix when capture is submitted. That eye-space rectangle crops the D3D11 mirror texture returned by `GetMirrorTextureD3D11` and is encoded as JPEG.

Capture is explicitly configured as `left-eye` or `right-eye`. The selected mirror texture remains acquired across captures, is primed for two compositor frames on first use, and is rectified with the HMD pose stored in the matching compositor frame timing. Legacy stereo values migrate to `left-eye`.

Selection remains a standalone OpenVR overlay. Every persistent capture, result or WPF window is a separate standard Quad Overlay with its own absolute transform and D3D11 texture. The runtime updates overlay sort order from HMD distance for deterministic application-owned stacking; game depth is intentionally outside the contract. Capture and result textures upload only when dirty, while registered WPF windows can opt into a bounded refresh rate.

## Result lifecycle

Captures and results have no fixed slot or count limit. A submission creates a result plane, streaming backend updates replace that plane's text, and completion only changes its status.

Hand proximity to a plane selects the interaction target; either grip moves and freely rotates it. The opposite hand casts a controller-space ray through `ComputeOverlayIntersection`. Only the panel cursor is drawn: there is no world-space laser. A trigger press captures the current overlay and UV until release, preventing target changes from leaving a control pressed.

The result surface is rendered from an offscreen read-only WPF `TextBox`, whose native `ScrollViewer` owns wrapping, scroll extent and top/bottom clamping. Right-stick movement scrolls the touched result. Right-stick click toggles all persistent overlays; a new capture restores them automatically.

`IWpfSpatialOverlayHost` is the extension boundary for arbitrary same-process WPF windows. `WpfWindowOverlaySource` renders the visual tree to a frozen bitmap and converts normalized overlay UVs into `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_LBUTTONUP` and `WM_MOUSEWHEEL` messages for that window's HWND. It never moves the desktop cursor or injects global input.

## Replaceable boundaries

- `SteamVrCompositorCaptureService` owns D3D11 mirror-texture readback and eye projection.
- `TranslationPipeline` coordinates capture, persistence and translation.
- `ITranslationBackend` isolates cloud provider protocol.
- `WasapiCommandRecorder` records the locked-region command gesture without delaying short-press translation.
- `SenseVoiceCommandTranscriber` owns the resident worker used only by custom-command submissions.
- `ICustomCommandBackend` receives the captured image and plain transcription; it streams plain text rather than structured JSON.
- `SpatialQuadOverlayManager` owns one OpenVR handle and D3D11 texture queue per persistent object, including intersection and distance sorting.
- `IWpfSpatialOverlayHost` exposes window registration, invalidation and removal without exposing OpenVR handles to extension code.
- `SteamVrTranslationRuntime` owns OpenVR calls on one polling thread.

The main voice-input project is intentionally not referenced. The SenseVoice executable protocol and portable file layout are compatible, while configuration and lifecycle ownership remain local to this application.
