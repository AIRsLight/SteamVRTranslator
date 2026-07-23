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

Selection remains a standalone OpenVR overlay. Every persistent capture, result or WPF window is one logical object backed by layered standard Quad Overlays: a static content layer, a transparent toolbar/highlight layer, a compact close-progress layer and a 64-pixel cursor layer. Persistent content textures use aspect-matched 512, 1024 or 2048-pixel long-edge tiers; ordinary capture and WPF content cap at 1024 while rendered HTML uses a native 2048 tier. Cursor motion only updates the child overlay transform, and close progress uploads a 256-pixel texture, so neither path redraws the content texture. The runtime reserves adjacent sort orders for each object's layers, then orders logical objects by HMD distance for deterministic application-owned stacking; game depth is intentionally outside the contract. Capture and result content textures upload only when dirty, while registered WPF windows can opt into a bounded refresh rate.

## Result lifecycle

Captures and results have no fixed slot or count limit. A submission creates a result plane, streaming backend updates replace that plane's text, and completion only changes its status.

Hand proximity to a plane selects the interaction target; either grip moves and freely rotates it. The opposite hand casts a controller-space ray through `ComputeOverlayIntersection`. Only the panel cursor is drawn: there is no world-space laser. A trigger press captures the current overlay and UV until release, preventing target changes from leaving a control pressed.

The result surface is rendered from an offscreen read-only WPF `TextBox`, whose native `ScrollViewer` owns wrapping, scroll extent and top/bottom clamping. Right-stick movement scrolls the touched result. Right-stick click toggles all persistent overlays; a new capture restores them automatically.

`IWpfSpatialOverlayHost` is the extension boundary for arbitrary same-process WPF windows. `WpfWindowOverlaySource` renders the visual tree to a frozen bitmap and converts normalized overlay UVs into `WM_MOUSEMOVE`, `WM_LBUTTONDOWN`, `WM_LBUTTONUP` and `WM_MOUSEWHEEL` messages for that window's HWND. It never moves the desktop cursor or injects global input.

## Prompt configuration

Markdown translation, HTML layout translation, custom voice commands, voice translation and subtitle translation each own a system prompt, task prompt, provider override and generation settings. Generation settings are independent by prompt purpose and include Qwen-compatible thinking, temperature, top-p and an optional maximum output-token limit. The editable task prompt contains only behavioral instructions: the pipeline appends the selected target language or transcribed user command as required context after it. Prompt edits are debounced, persisted atomically and passed to `TranslationPipeline.ApplyLiveSettings`, so the next request observes a complete prompt snapshot without recreating the SteamVR runtime.

## Replaceable boundaries

- `SteamVrCompositorCaptureService` owns D3D11 mirror-texture readback and eye projection.
- `TranslationPipeline` coordinates capture, persistence and translation.
- `ITranslationBackend` isolates cloud provider protocol.
- `WasapiCommandRecorder` records the locked-region command gesture without delaying short-press translation.
- `SenseVoiceCommandTranscriber` owns the resident worker used by custom-command and VRChat PTT submissions. It selects the CPU executable or the downloaded Vulkan executable and passes the configured physical-device index to the same worker protocol.
- `ICustomCommandBackend` receives the captured image and plain transcription; it streams plain text rather than structured JSON.
- `SpatialQuadOverlayManager` owns one OpenVR handle and D3D11 texture queue per persistent object, including intersection and distance sorting.
- `IWpfSpatialOverlayHost` exposes window registration, invalidation and removal without exposing OpenVR handles to extension code.
- `SteamVrTranslationRuntime` owns OpenVR calls on one polling thread. It passively attaches as `VRApplication_Background` only after `vrserver` and `vrcompositor` are present; after a quit event it waits for both processes to disappear before rearming, so this application never owns the SteamVR lifecycle.
- `VibeVoiceApiTranscriber` is the subtitle-side HTTP client. It submits a complete audio file so VibeVoice can preserve native speaker context across the recording, then maps returned anonymous speaker labels into the subtitle session.
- `SteamVRTranslator.VibeVoice.Server` is a separate ASP.NET Core process. It owns CrispASR runtime/model installation, the resident inference child process and the authenticated OpenAI-compatible transcription endpoint. The desktop application never loads VibeVoice weights into its own process.

The main voice-input project is intentionally not referenced. The SenseVoice executable protocol and portable file layout are compatible, while configuration and lifecycle ownership remain local to this application.
