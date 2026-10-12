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
`docs/2d-to-3d-research.md` compares iw3 (nunif), Depth3D and others with this pipeline and
ranks what to adopt; `docs/improvement-plan.md` turns that into phased work with the maths and
the measures each phase is judged by.

## Build

```
dotnet build ImmersiveSBS.slnx -c Release   # Release for runs in the glasses (see below)
dotnet test ImmersiveSBS.slnx
dotnet test ImmersiveSBS.slnx --filter "FullyQualifiedName~TheaterTests"   # one class or test
```

Tests (`tests/ImmersiveSBS.Tests`, xUnit) cover the pure logic and run the GPU warp on a real
D3D11 device; `DepthEstimatorTests` runs the TensorRT model and skips itself without an RTX GPU
or the model files in `%LOCALAPPDATA%\ImmersiveSBS\models`. Verify with tests and the app's log
(`%LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log`; tests write `ImmersiveSBS.tests.log` beside it)
rather than throwaway harnesses. The log's `Stats:` line every 10 s (fps, latency, VRAM,
memory, AI 3D state, then `Measures:` with the improvement plan's M1–M7 and `GPU ms per frame:`)
and the `Audio:` line every 10 s while the page delays the sound are the main evidence from a
run in the glasses.

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
  model file changes), the browser link (and the page's delayed sound) and theater mode, and pushes settings to the session
  live through `ApplyAi` (`StereoSettings`).
- **`Session`** runs one render thread: capture (`Capture/DuplicationCapture`, or
  `GraphicsCapture`) → `Depth/AiStereo.Process` → `Theater/TheaterUi.Build` → `Renderer.Render`
  → present on a flip-model swap chain in `PresenterWindow` (glasses) or `MirrorWindow` (a
  window on a real monitor). It redraws only for a new frame, new depth, changed settings or the
  on-screen layer.
- **AI 3D** (`Depth/`): the video is only ever the one the browser extension reports (nothing
  is guessed from the picture: the old `RegionTracker` took scrolling pages, animations and the
  desktop for videos and was removed); without the extension only Whole screen converts. An 8×8
  luma grid says which frames changed; `Native3DDetector` passes already-3D video through; `DepthService` runs the model on its own
  thread (`DepthEstimator`: ONNX Runtime with the TensorRT for RTX execution provider, found as
  an installed Windows package; compiled engines cached per GPU in
  `%LOCALAPPDATA%\ImmersiveSBS\engines`) and post-processes at model resolution on the CPU
  on a second thread, alongside the next frame's model, each step split over bands of rows on
  up to 8 cores (`RowBands`; the buffer and matched timing need the depth on the CPU anyway)
  (`DepthPostProcessor`: normalisation,
  temporal smoothing, then on the output only `EdgeFilters` anti-aliasing and soft edges, page
  overlays, static overlays, `CaptionLock`); `StereoSynth` builds both eyes with compute shaders
  (guided upsample, optional snap and dilation, slope limit, then the forward splat or the lookup
  warp). Only frames whose picture changed in the region go to the model (the capture's dirty
  rectangles, checked against the luma grid after the present). Models are static-shape files
  `{stem}_{H}x{W}.onnx` (`DepthModel`).
- **Timing** (`StereoSettings.Timing`, `BufferSeconds`): live; matched (`MatchedDelay` sets a
  delay of 50–300 ms from the measured depth age, held frames get their own or interpolated
  depth; Auto turns it on when depth runs over 60 ms behind); or the buffer.
- **Delayed sound: only in the page.** The app never captures, replays or turns down any
  program's sound (the old process-loopback path turned the browser down to 1 % and was
  removed). In Chrome/Edge the extension (`extension/`, talking to `Browser/BrowserLink` over a
  WebSocket on 127.0.0.1:47391) delays the followed video's tab through Web Audio. The
  controller tells the session whether it can (`PageCanDelay`); without it there is no buffer
  (a saved buffer behaves as Auto; its choices are greyed in the tray, settings and theater),
  and matched timing is capped at 90 ms with the sound live. With it, the session reports a
  `DelayRegion` and `DelaySeconds`, the page plays the sound that late, the controller tells the
  session how late it plays now (`SoundDelay`), and `VideoDelayBuffer` shows frames that late
  (each with its own crop) with depth averaged over neighbours (the buffer, with the depth
  thread looking ahead) or interpolated (matched). The picture's delay follows the sound's by
  holding a frame when it grows (a new page delay line is silent for its length), never by
  jumping back. The extension also reports each video's exact place; the controller follows one
  (the same element while it stays visible and on top, else the largest playing one on the
  virtual monitor, or the one the user picked with the theater button or the pick shortcut) and
  hands it to the session as `BrowserVideo`, the region AI 3D converts.
  With protocol 2 the app follows that video (`BrowserLink.Follow`): its frame events, state,
  overlay and caption boxes and a clock offset arrive in a `PageVideo` the render thread reads
  directly.
- **Theater**: a player window (`Theater/TheaterWindow`) opens over the whole virtual monitor,
  left out of the capture (`WDA_EXCLUDEFROMCAPTURE`), so the page's video is still captured under
  it while the render thread draws it in 3D filling both eyes; every click and key lands on the
  window, not the page (a double click there once turned YouTube full screen and cropped the
  theater). It is a tool window, which Chromium ignores when it decides a browser window is
  covered (a covered one stops painting), and `Native.RootWindowAt` looks through it to the
  browser. Keys: Esc or a double click leave, Space/K play/pause, ←/→ and J/L seek 10 s (the
  extension's `seek`). 3D settings and Exit sit at the top; a player's bar at the bottom (seek
  bar, time, back / play-pause / on, mute) works from the followed video's `state` messages
  (time, duration, muted) and stays while it is paused. Buttons exist only in the glasses, so the render thread publishes their
  desktop rectangles (`TheaterButtons`) and `TheaterInput`, a low-level mouse hook, swallows
  clicks on them. The "3D theater" button is offered only over the videos the extension reports
  (`Session.PageVideos`, from the browser window under the cursor; `TheaterUi.OfferedVideo`), and
  the theater opens on the one pressed and follows the page's box for it; without the extension
  there is no button and no theater (the tray and the shortcut say why).

## Things measured the hard way

- A Debug build runs the depth post-processing 5–7 times slower (55 ms against 8 ms a frame at
  518×294 with the edge steps, single-threaded); the app logs a warning. Use Release for runs in
  the glasses, and only trust timings from Release tests. Split over cores (16 bands on at most
  8 threads), Release takes 3.3 ms at 518×294 and 5.7 ms at 756×420 against 8.5 and 16.5 on one
  core (20–26 ms in the app before). `ParallelSpeedTests` measures both on 48 frames of a real
  clip with the real model's depth, when `%LOCALAPPDATA%\ImmersiveSBS\samples` has the clip
  (Big Buck Bunny 720p, 10 s, CC-BY; not in the repo); timing tests share the "Timing" xUnit
  collection, which runs alone.
- The forward splat opens a hole wherever nearness changes by more than 2/S per px
  (improvement-plan.md §2.1); soft edges alone do not prevent it for big steps, the slope limit
  does. Holes at the region's side edges are not counted in M2.
- GPU timings from tests mean nothing while the GPU idles between frames (a laptop GPU clocks
  down tenfold): keep it busy, as `EdgePipelineTests` does.

- `BlendDescription` needs the four-argument form (alpha factors Zero/One), or
  `CreateBlendState` fails with E_INVALIDARG.
- `DepthService.TryTake` swaps frames with the caller; frames from another model size must not
  be recycled.
- TensorRT for RTX runs a model at the precision of its file, so speed needs fp16 files.
  `onnxconverter_common`'s fp16 converter breaks on a dynamic export's shape arithmetic (casts
  around Shape/Gather/Range); fold it into constants first (ORT's basic optimizer on the static
  fp32 model), then convert. Cached engines are named by a fingerprint of the model file.
- Never reset a delay line on a passing change: emptying `VideoDelayBuffer` freezes the picture
  for the whole delay, restarting the delayed sound silences it as long, and jumping the audio
  read position is a click.
- `createMediaElementSource` reroutes an element for good, and through a suspended
  `AudioContext` it goes silent: the extension attaches only playing elements, only once its
  context runs, and never cross-origin or protected media (Web Audio gets silence from them).

## Rules carried over from DesktopSbS

- The app takes over the glasses. Claude may start the Release build for test runs and close
  the copy it started, but never one the user started; any other launch needs asking first.
- Do not install or remove the virtual display driver, change display settings or scaling, or
  call `SetDisplayConfig`. The user changes the display layout in Windows Settings.
- Do not kill the user's running session; if the exe is locked, build another configuration.
- Harnesses and experiments are throwaway and stay out of the repo (use the scratchpad).
- Commit only when asked. Commit straight to `main`: no feature branches (the user's choice).
- DRM-protected content is out of scope.
- Ported DesktopSbS files keep a header line naming the original file.
