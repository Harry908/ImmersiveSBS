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
`docs/code-map.html` (open in a browser) draws the components, threads, the main activities and
one frame's journey, and maps every source file; refresh it when the architecture changes.
`docs/packaging.md` is how a release is built and what is still to decide about shipping it.

## Build

```
dotnet build ImmersiveSBS.slnx -c Release   # Release for runs in the glasses (see below)
dotnet test ImmersiveSBS.slnx
dotnet test ImmersiveSBS.slnx --filter "FullyQualifiedName~TheaterTests"   # one class or test
.\packaging\publish.ps1 -Models dad-base   # the portable package in artifacts\ (docs/packaging.md)
.\packaging\build-installer.ps1            # the one-click installer, app + models + the driver
```

The installer installs the Virtual Display Driver when there is none: never run it on this PC
without the user's go-ahead (it installs into Program Files; its driver check skips the driver
here, which already has it).

The release number is `<Version>` in `src/ImmersiveSBS/ImmersiveSBS.csproj` (`AppController.Version`:
the log's first line, the Settings window's title, the package's name). `THIRD-PARTY-NOTICES.md`
lists every shipped library and model with its licence; keep it in step with the packages, and
never ship a non-commercial model (Depth Anything V2 Base/Large, Video Depth Anything Large).

Tests (`tests/ImmersiveSBS.Tests`, xUnit) cover the pure logic and run the GPU warp on a real
D3D11 device; `DepthEstimatorTests` runs the TensorRT model and skips itself without an RTX GPU
or the model files. Verify with tests and the app's log
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
  It runs the Stage A guards (`Input/CursorGuard`, `StrayWindowGuard`; always on, no setting)
  only while the glasses show the virtual monitor, owns the shared `DepthService` (loaded at start, swapped when the
  model file changes), the browser link (the theater, the page's delayed sound), and pushes
  settings to the session live through `ApplyAi` (`StereoSettings`). At exit it moves windows
  off the virtual monitor and takes it off the desktop; at start it puts it back first
  (`VirtualMonitorSwitch`, the one change it makes to the layout).
- **`Session`** runs one render thread: capture (`Capture/DuplicationCapture`, or
  `GraphicsCapture`) → `Depth/AiStereo.Process` → `Theater/TheaterUi.Build` (the model badge) →
  `Renderer.Render` → present on a flip-model swap chain in `PresenterWindow` (glasses) or
  `MirrorWindow` (a window on a real monitor). It redraws only for a new frame, new depth,
  changed settings or the badge.
- **The extension is part of the app** (`extension/`, shipped in the app's folder, loaded
  unpacked; `docs/extension-first-plan.md`). It talks to `Browser/BrowserLink` over a WebSocket
  on 127.0.0.1:47391, protocol 3 only (`BrowserLink.Protocol`; any other is an old copy,
  refused with a tray message to reload it). For browser video the page is the source of
  truth: the app guesses nothing about pages, windows or players.
- **AI 3D** (`Depth/`) converts the theater's video (below) or, as a setting, the whole screen;
  "Off" means only the theater. What the video is (2D, or side-by-side or top-bottom 3D shown as
  is, `Native3DMode`) is the user's choice, not detected. `DepthService` runs the model on its own
  thread (`DepthEstimator`: ONNX Runtime with the TensorRT for RTX execution provider, found as
  an installed Windows package, or asked of Windows ML's catalog when missing; compiled engines cached per GPU in
  `%LOCALAPPDATA%\ImmersiveSBS\engines`) and post-processes at model resolution on the CPU
  on a second thread, alongside the next frame's model, each step split over bands of rows on
  up to 8 cores (`RowBands`; the theater's depth by frame needs it on the CPU anyway)
  (`DepthPostProcessor`: normalisation,
  temporal smoothing along the picture's motion (`MotionField`), then on the output only `EdgeFilters` anti-aliasing and soft edges, the
  player's boxes, static overlays, `CaptionLock` for burned-in subtitles); `StereoSynth` builds
  both eyes with compute shaders (guided upsample, optional snap and dilation, slope limit, then
  the forward splat or the lookup warp). Only new pictures go to the model: in the theater the
  frames its player sends as the video plays (`PagePictures`: JPEG at the model's input size,
  depth filed by line and time in the video, the frame shown found by the player's draw reports),
  or, when it cannot send them (another site's video), the frames it reports drawing; for the
  whole screen each new capture. Models are static-shape files `{stem}_{H}x{W}.onnx`
  (`DepthModel`), looked for in the app's `models` folder (a package ships them there), then in
  `%LOCALAPPDATA%\ImmersiveSBS\models`; the log's "Depth: loaded" line names the file. Video Depth Anything Small (`VideoDepthEstimator`, a trial) carries state:
  two graphs (first frame, step with 31 past states), the states in a ring of GPU tensors
  through I/O binding, reset at seeks and scene cuts.
- **Timing** (`StereoSettings.Timing`, `BufferSeconds`): live; matched (`MatchedDelay` sets a
  delay of 50–300 ms from the measured depth age; Auto turns it on when depth runs over 60 ms
  behind); or the buffer (2, 3 or 4 s; 2 by default and at least). Only the theater is ever delayed, and only by its page.
- **Delay: only in the page, sound and picture together.** The app never delays, captures,
  replays or turns down any sound, and never shows the screen late: it is always shown as it
  is. The extension delays the theater video's tab's sound through Web Audio, and its player
  shows the picture as late itself (WebGL: each presented frame kept as luma and half-size
  chroma, 1.5 bytes a pixel at the size shown or the video's own if smaller, slots no longer
  needed given back (`trimSlots`), stamped on the audio clock and drawn when its
  sound is heard), with the caption read with each frame, its bar, panel and spinner live over
  it; each video at its own frame rate (frame interpolation to 60 fps was tried and removed on
  2026-10-26: no better 3D, only more work). The session reports `DelayRegion`/`DelaySeconds`; the controller asks the page for that
  delay (`SetDelay`: the sound's with the trim and output latency, the picture's without) and
  tells the session how late the sound plays now (`SoundDelay`: the model looks that far
  ahead). Protected and cross-site media cannot be delayed (`BrowserCanDelay`): live then. No
  gaps for a watched video (never for a live stream, the page's `live`): a pause suspends the
  AudioContext, which holds sound and picture; at the end the tail plays out; a seek starts the
  delay line again, drops the kept frames not shown and starts a new line (the page's count of
  jumps, which keys the depth); the player shows a spinner until the new place's first frame;
  a paused seek shows its frame at once.
- **Theater** (`content.js` "The 3D theater"): while the app says the glasses are in 3D mode
  (`BrowserLink.SetStatus`, with the 3D panel's values, the stretch, what the video is, and
  whether a model is loading: the player then waits, paused, `holdForModel`), a
  "3D theater" button shows over a video the mouse is on; pressed (a real click, so the page may
  go full screen), a player in a closed shadow root covers the page, goes full screen and draws
  the video on a WebGL 2 canvas at the picture's size on the screen (stretched if set; a 2D
  canvas at the video's size for a video it may not read). It stays full screen while open:
  leaving full screen closes it (no windowed theater), and the page is back in its window. Opened
  while the page is in its own full screen, the player goes inside that element and uses it
  instead of going full screen over it: with two levels, YouTube missed the end of its own and
  stayed in its full-screen layout after the theater closed. One frosted bar (seek bar, time, ±10 s, play/pause, mute, 3D
  panel, exit; keys Space/K, ←/→ J/L, M, Esc; a click on the picture plays or pauses), a 3D
  panel whose buttons come back as `command` messages, and the page's captions copied in (its
  caption boxes within 6 ancestors of the video, and text tracks; read only when a
  MutationObserver on the page's player or a `cuechange` says they changed, and once a second
  anyway: a read forces the page's layout). The bar, the panel and the spinner are one box each,
  which the renderer draws from the capture flat in both eyes (`AiStereo.LiveControls`), the
  depth under them (and 3 model px around) set to the screen plane (`PageBoxes.Flatten`); the
  caption is held flat in front of the scene at render time (`CaptionLock.ApplyGiven`). While
  open it reports itself (`theater`: its picture in viewport CSS px, the px ratio, whether the
  viewport is the screen), each frame it draws on the app's clock (with its line), its state,
  its own boxes, and each frame the video presents at the model's size (`picture`, only to the
  app; the status says the size); `TheaterReport` maps the picture to the screen. The controller follows the
  newest open theater, if the browser is full screen on the virtual monitor (`FullScreenOn`),
  and sets `Theater` (AI 3D on for it). From the tray (no click in the page) the worker opens it
  on the largest playing video of the tab in front, fills the browser window and puts the
  window in full screen. Removed on 2026-10-12: the app's own button, player window and mouse
  hook, in-place conversion of page videos (following a video through window geometry, page
  layout and player overlays), already-3D detection, dirty-rectangle and luma-grid change
  detection, and the app's stretch.

## Things measured the hard way

- A Debug build runs the depth post-processing 5–7 times slower (55 ms against 8 ms a frame at
  518×294 with the edge steps, single-threaded); the app logs a warning. Use Release for runs in
  the glasses, and only trust timings from Release tests. Split over cores (16 bands on at most
  8 threads), Release took 2.2 ms at 518×294 and 3.1 ms at 756×420 against 4.7 and 8.7 on one
  core on 48 consecutive frames (20–26 ms in the app before; the 3.3/5.7 first recorded were one
  picture 48 times, see below); with the smoothing following the motion (2026-10-25), 6.4 and
  11.7 ms (19.7 and 42.1 on one core), alongside the model's 5–11. `ParallelSpeedTests` measures both on 48 frames of a real
  clip with the real model's depth, when `%LOCALAPPDATA%\ImmersiveSBS\samples` has the clip
  (Big Buck Bunny 720p, 10 s, CC-BY; not in the repo); timing tests share the "Timing" xUnit
  collection, which runs alone.
- GPU timings from tests mean nothing while the GPU idles between frames (a laptop GPU clocks
  down tenfold): keep it busy, as `EdgePipelineTests` does.

- `BlendDescription` needs the four-argument form (alpha factors Zero/One), or
  `CreateBlendState` fails with E_INVALIDARG.
- Never reset a delay line on a passing change: dropping the kept frames freezes the picture
  for the whole delay, restarting the delayed sound silences it as long, and jumping the audio
  read position is a click.
- A captured screen has the page's video and its controls merged into one picture: delaying it
  in the app (the buffer, matched timing, removed 2026-10-19) brought the controls seconds late,
  and patching them in from the live screen showed the video at another time through them, a
  see-through hole that stayed where a panel had been for the length of the delay (the video
  under an old panel is in no capture). Two layers exist only in the page: it delays the
  picture itself, and the app shows the screen as it is.
- `MediaComposition.GetThumbnailsAsync` with times 1/30 s apart returns one picture for all of
  them; `SampleClip` asks for one thumbnail per call (the earlier "consecutive frames" were one
  picture repeated).

More in `src/ImmersiveSBS/Depth/CLAUDE.md` (the depth path) and `extension/CLAUDE.md` (the page).

## Rules carried over from DesktopSbS

- The app takes over the glasses. Claude may start the Release build for test runs and close
  the copy it started, but never one the user started; any other launch needs asking first.
- Do not install or remove the virtual display driver, change display settings or scaling, or
  call `SetDisplayConfig`. The user changes the display layout in Windows Settings. The one
  exception is the app's own (`Displays/VirtualMonitorSwitch`, since 2026-10-26, the user's
  request): the virtual monitor off the desktop at exit and back where it was at start
  (`AppSettings.DisconnectVirtualMonitor`, on by default), only that path; the virtual display
  driver's own control cannot (its pipe's `SETDISPLAYCOUNT 0` still loads one monitor). Running
  it for a test needs the user's go-ahead.
- Do not kill the user's running session; if the exe is locked, build another configuration.
- Harnesses and experiments are throwaway and stay out of the repo (use the scratchpad).
- Commit only when asked. Commit straight to `main`: no feature branches (the user's choice).
- DRM-protected content is out of scope.
- Ported DesktopSbS files keep a header line naming the original file.
