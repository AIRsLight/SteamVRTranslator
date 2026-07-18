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

The overlay is transformed into tracking space with `SetOverlayTransformAbsolute`. Each corner is projected through the current left and right eye matrices when capture is submitted. Those two eye-space rectangles crop the D3D11 mirror textures returned by `GetMirrorTextureD3D11` and are combined into one PNG.

Capture is explicitly configured as `left-eye` or `right-eye`. The selected mirror texture remains acquired across captures, is primed for two compositor frames on first use, and is rectified with the HMD pose stored in the matching compositor frame timing. Legacy stereo values migrate to `left-eye`.

Selection remains a standalone OpenVR overlay. Persistent captures, results and both tracked controller render models are drawn into one stereo D3D11 scene with a shared depth buffer per eye, then submitted through two projection overlays. This guarantees per-pixel occlusion among application-owned objects; game depth is intentionally outside the contract. Text and capture surfaces only upload when dirty, while the lightweight stereo scene is redrawn for current HMD and controller poses.

## Result lifecycle

Captures and results have no fixed slot or count limit. A submission creates a result plane, streaming backend updates replace that plane's text, and completion only changes its status.

Hand proximity to a plane selects the interaction target; either grip moves and freely rotates it. The result surface is rendered from an offscreen read-only WPF `TextBox`, whose native `ScrollViewer` owns wrapping, scroll extent and top/bottom clamping. Right-stick movement scrolls the touched result. Right-stick click toggles the complete stereo scene, including controller models; a new capture restores it automatically.

## Replaceable boundaries

- `SteamVrCompositorCaptureService` owns D3D11 mirror-texture readback and eye projection.
- `TranslationPipeline` coordinates capture, persistence and translation.
- `ITranslationBackend` isolates cloud provider protocol.
- `WasapiCommandRecorder` records the locked-region command gesture without delaying short-press translation.
- `SenseVoiceCommandTranscriber` owns the resident worker used only by custom-command submissions.
- `ICustomCommandBackend` receives the captured image and plain transcription; it streams plain text rather than structured JSON.
- `SteamVrTranslationRuntime` owns OpenVR calls on one polling thread.

The main voice-input project is intentionally not referenced. The SenseVoice executable protocol and portable file layout are compatible, while configuration and lifecycle ownership remain local to this application.
