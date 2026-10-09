# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

ImmersiveSBS shows a Windows desktop in video glasses in hardware 3D mode (RayNeo GT Max:
3840×1080 side-by-side, 1920×1080 per eye) by capturing a 1920×1080 virtual monitor and drawing
it into both halves, and can convert 2D video to stereo 3D with a depth model. It succeeds the
overlay design of DesktopSbS (`D:\Desktop\DesktopSbS`, GPL-3.0); this repo is GPL-3.0 too and
ports code from it.

`docs/plan.md` is the design and phase plan; `progress.md` is the living record (machine,
displays, driver install, what each phase did and what is open). Read both before starting a
phase and keep them current.
`docs/video-3d-plan.md` is the earlier AI-video plan; only §0 and §4–§7 still apply.

## Build

```
dotnet build ImmersiveSBS.slnx -c Debug
dotnet test ImmersiveSBS.slnx
dotnet test ImmersiveSBS.slnx --filter "FullyQualifiedName~TheaterTests"   # one class or test
```

Tests (`tests/ImmersiveSBS.Tests`, xUnit) cover the pure logic and run the GPU warp on a real
D3D11 device; `DepthEstimatorTests` runs the TensorRT model and skips itself without an RTX GPU
or the model files in `%LOCALAPPDATA%\ImmersiveSBS\models`. `AudioLoopbackTests` play quiet
tones from the test process. Verify with tests and the app's log
(`%LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log`; tests write `ImmersiveSBS.tests.log` beside it)
rather than throwaway harnesses. The log's `Stats:` line every 10 s (fps, latency, VRAM,
memory, AI 3D state) and the `Audio:` line every 10 s while sound is delayed are the main
evidence from a run in the glasses.

.NET 10 SDK, SDK-style project, x64 only, target `net10.0-windows10.0.19041.0` (WinRT APIs such
as Windows.Graphics.Capture come from the target framework). Per-monitor v2 DPI awareness is set
in `app.manifest`: every coordinate is a physical pixel. WPF and WinForms (tray) are both on;
`System.Windows.Forms` and `System.Drawing` are not implicit usings, so import or alias them
per file. Source files mix CRLF and LF line endings; keep each file's own.

## Architecture

- **`AppController`** (UI thread) owns everything. `Evaluate` reads the display layout (CCD
  identities in `Displays/`; `GlassesStateEvaluator` decides 3D/2D from the glasses' mode) and
  derives the wanted `SessionOptions`; `ReconcileAsync` keeps or replaces the `Session` by key.
  It runs the Stage A guards (`Input/CursorGuard`, `StrayWindowGuard`) only while the glasses
  show the virtual monitor, owns the shared `DepthService` (loaded at start, swapped when the
  model file changes), the delayed sound and theater mode, and pushes settings to the session
  live through `ApplyAi` (`StereoSettings`).
- **`Session`** runs one render thread: capture (`Capture/DuplicationCapture`, or
  `GraphicsCapture`) → `Depth/AiStereo.Process` → `Theater/TheaterUi.Build` → `Renderer.Render`
  → present on a flip-model swap chain in `PresenterWindow` (glasses) or `MirrorWindow` (a
  window on a real monitor). It redraws only for a new frame, new depth, changed settings or the
  on-screen layer.
- **AI 3D** (`Depth/`): `RegionTracker` finds the playing video on an 8×8 luma grid;
  `Native3DDetector` passes already-3D video through; `DepthService` runs the model on its own
  thread (`DepthEstimator`: ONNX Runtime with the TensorRT for RTX execution provider, found as
  an installed Windows package; compiled engines cached per GPU in
  `%LOCALAPPDATA%\ImmersiveSBS\engines`) and post-processes at model resolution on the CPU
  (`DepthPostProcessor`); `StereoSynth` builds both eyes with compute shaders. Models are
  static-shape files `{stem}_{H}x{W}.onnx` (`DepthModel`).
- **Buffer** (delayed picture and sound): the session reports a `DelayRegion`; the controller
  finds the process under it and starts `Audio/AudioDelay`; only once that runs is the session
  told `AudioDelayed`, and `VideoDelayBuffer` then shows frames that late with depth averaged
  over neighbours.
- **Theater**: the video fills both eyes; buttons exist only in the glasses, so the render
  thread publishes their desktop rectangles (`TheaterButtons`) and `TheaterInput`, a low-level
  mouse hook, swallows clicks on them.

## Things measured the hard way

- Process loopback audio is taken after the app's session mute and session volume (amplitude
  v²). The live sound is therefore turned down to v = 0.01, never muted, and the capture boosted
  by 10⁴ (`SessionAttenuator`). Turned-down sessions are recorded in
  `attenuated-sessions.txt` and restored at the next start after a crash.
- Apps keep idle audio sessions on every output they ever used; only an active session says
  where the app is heard. The glasses' audio output resets when they switch 2D/3D.
- `BlendDescription` needs the four-argument form (alpha factors Zero/One), or
  `CreateBlendState` fails with E_INVALIDARG.
- `DepthService.TryTake` swaps frames with the caller; frames from another model size must not
  be recycled.

## Rules carried over from DesktopSbS

- Do not launch the app without asking: it takes over the glasses.
- Do not install or remove the virtual display driver, change display settings or scaling, or
  call `SetDisplayConfig`. The user changes the display layout in Windows Settings.
- Do not kill the user's running session; if the exe is locked, build another configuration.
- Harnesses and experiments are throwaway and stay out of the repo (use the scratchpad).
- Commit only when asked. Commit straight to `main`: no feature branches (the user's choice).
- DRM-protected content is out of scope.
- Ported DesktopSbS files keep a header line naming the original file.
