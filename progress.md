# Progress

The living record of ImmersiveSBS: what each phase changed, how it was verified, and what is
still open. Read it with `docs/plan.md` before starting a phase and update it after. The newest
work is at the end, before "Carried over from the DesktopSbS session".

## Where things stand (2026-10-20)

- **Version 1.0.0** (2026-10-20), extension 1.8.3 (protocol 3). In daily use on the development setup:
  the desktop in both eyes, the 3D theater on YouTube and WeTV with the 2 s buffer, captions.
- **The theater's two layers live in the page**: it delays picture and sound together and
  sends the frames ahead for depth; the app shows the screen as it is. Pause, play and seek
  have no gaps; the controls answer at once and are drawn flat.
- **Models**: Distill-Any-Depth Base (default), Depth Anything V2 Small, Depth Anything 3 Small
  and Base, Video Depth Anything Small (a trial: steadier, but short of its go/no-go).
- **A one-click installer builds** (`docs/packaging.md`): the app, the default model and the
  Virtual Display Driver (only when missing), in 575 MB; a portable zip too. Not yet run end to
  end on a PC without the driver; the other shipping decisions are listed there.
- **Tests**: 167, Release. The code map is `docs/code-map.html`.

## Development machine (2026-10-07)

- ASUS laptop, Windows 11 Pro build 26300. NVIDIA GeForce RTX 3060 Laptop GPU (6 GB) and
  Intel Iris Xe. The glasses and the Acer are wired to the RTX.
- .NET SDK 10.0.300. Visual Studio 18 MSBuild at
  `C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe`.
  No `gh` CLI.
- Displays (GDI names change between sessions; never key on them):

  | Display | Monitor | Mode | Position |
  |---|---|---|---|
  | RayNeo GT Max | `DISPLAY\TCL03D4` | 2D: 1920×1080 @ 60/120 Hz; 3D: 3840×1080 (refresh not yet measured) | above the Acer |
  | Acer EB323QU | `DISPLAY\ACR0E9F` | 2560×1440 @ 144, primary | 0,0 |
  | Built-in panel | `DISPLAY\BOE0A6D` | 1920×1200 (Iris Xe) | left of the Acer |
  | Virtual monitor | `DISPLAY\MTT1337` ("VDD by MTT") | 1920×1080 | right of the Acer |

- The glasses also expose a USB HID device "RayNeo AR Glasses" (`VID_3941&PID_AF50`), probably
  the IMU and control channel; RayNeo's protocol is closed. Their sound is
  "SmartGlasses (NVIDIA High Definition Audio)", carried over DisplayPort.

## P0 — Repo (done, 2026-10-07)

Skeleton, licence, `docs/plan.md`, `docs/video-3d-plan.md`. Pushed to
https://github.com/Harry908/ImmersiveSBS.

## P1 — Prototype (in progress)

### Virtual display driver (installed 2026-10-07)

- VirtualDrivers Virtual Display Driver **25.7.23**
  (`https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VDD.Control.25.7.23.zip`,
  SHA-256 `a701f2272e9fcf382849b24f913c6dd07597b3b1116525f2e90182f019609154`). Driver and
  catalog signed by SignPath Foundation; `devcon.exe` signed by Microsoft. The zip's
  `SignedDrivers\x86\VDD` folder holds the amd64 driver despite the name.
- Installed by the user from an elevated prompt:
  `devcon.exe install MttVDD.inf Root\MttVDD`. Device `ROOT\DISPLAY\0000`, status OK.
  Remove with `devcon.exe remove Root\MttVDD` (admin) or Device Manager.
- Settings: `C:\VirtualDisplayDriver\vdd_settings.xml` — 1 monitor; GPU
  `NVIDIA GeForce RTX 3060 Laptop GPU`; 1920×1080, 2560×1440, 1280×720 at 60/120 Hz; hardware
  cursor on; logging on (`C:\VirtualDisplayDriver\Logs`). The log confirms
  "ASSIGNED GPU: NVIDIA GeForce RTX 3060 Laptop GPU". One `DXGI_ERROR_ACCESS_LOST` at first start,
  then the D3D device was created normally.

### Prototype code (2026-10-07; builds, not yet run on the glasses)

`dotnet build` is clean (no warnings). Vortice.Direct3D11 and Vortice.D3DCompiler 3.8.3.

- `DisplayLocator` (replaced in P2 by the ported display service): DXGI outputs keyed by the
  monitor PnP id from `EnumDisplayDevices`.
- `Capture/DuplicationCapture`: `DuplicateOutput1` (B8G8R8A8), copied into our own texture and
  released at once; reopens every 250 ms after access is lost (mode change, secure desktop).
  Pointer position and shape come separately; `PointerState` converts monochrome, colour and
  masked-colour shapes into a colour image and an XOR image.
- `Capture/GraphicsCapture`: WGC behind the switch. Free-threaded frame pool, newest frame only,
  cursor drawn by Windows (`IsCursorCaptureEnabled = true`). The yellow border stays on
  (switching it off needs a Windows 11 API); it is drawn on the virtual monitor and left out of
  the capture.
- `Presentation/PresenterWindow`: borderless `WS_POPUP`, topmost, `WS_EX_NOACTIVATE`, tool
  window. Hides the real cursor if it strays there. Created and pumped on the render thread.
  Follows the glasses by monitor id on `WM_DISPLAYCHANGE` (2D/3D switch).
- `Presentation/Renderer`: flip-discard swap chain, 2 buffers, waitable, maximum frame latency
  1. A target wider than 2.5:1 is side-by-side: the frame goes into both halves, otherwise it
  is drawn once. It is fitted per eye with integer offsets and point-sampled when 1:1. The
  cursor is drawn last, at the same position in both eyes: an alpha pass, then an XOR pass with
  blend `InvDestColor`/`InvSrcColor`.
- `Session`: render thread. Capture (16 ms timeout) → wait on the swap chain → fold in any newer
  capture → draw → `Present(1)`. Measured per second: capture fps, cursor updates, present fps,
  glasses refresh (from the `GetFrameStatistics` sync counts), capture→Present and
  capture→vblank latency (the capture's QPC against `SyncQPCTime` of that present), and process
  VRAM on the render adapter. Logged every 10 s to `%LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log`.
- `MainWindow` (replaced in P2 by the tray and the settings window): pickers, Start/Stop, live
  stats.

Headless check (a scratch harness using the real capture classes; nothing shown on the
glasses): all four displays enumerate. The virtual monitor and the glasses are both outputs of
the RTX, so capture and presentation share one adapter. Desktop Duplication opens `MTT1337` at
1920×1080 @ 60, B8G8R8A8, in video memory, and reports a 32×32 pointer shape with XOR pixels.
WGC captures it at 1920×1080. The desktop was static, so only one frame arrived from each:
both deliver frames only on change.

### Still open for P1 (needs the user and the glasses)

1. First run on the glasses: 3D mode, 2D mode and the switch between them, cursor shapes
   (arrow, I-beam, resize), the secure desktop (UAC) and recovery from it.
2. Measure with both capture methods (switch in the tray menu): capture→vblank latency, present
   fps against the glasses' refresh in 3D mode, GPU use (Task Manager), VRAM, text sharpness; a
   flashing test pattern filmed through the lens. The settings window shows the live numbers.
3. Set the virtual monitor's refresh rate to the measured 3D refresh rate if they differ.
4. Test whether the hardware 3D button still works with the glasses removed from the desktop
   (plan D2 Stage B).

## P2 — App shell (built 2026-10-07, not yet run)

Written before P1's first run, so it rests on the P1 design, not on P1 measurements. Builds
clean. The app is now a tray application; there is no main window.

Ported from DesktopSbS (header line names the original file):
`Interop/DisplayConfig.cs`, `Displays/DisplayInfo.cs`, `Displays/DisplayConfigurationService.cs`
(DPI via `GetDpiForMonitor`), `Displays/DisplayChangeMonitor.cs` (shell-restart event dropped),
`Displays/GlassesState.cs` (from `DisplayStereoState.cs`), `Displays/KnownGlasses.cs` (models
and EDID keys only), `Input/GlobalKeyboardHook.cs`, `Tray/TrayIcon.cs`, `Theme/ThemeManager.cs`,
`Theme/FluentMenuRenderer.cs`, `Theme/Fluent.xaml`, `Light.xaml`, `Dark.xaml`,
`Log.cs`. The icon is DesktopSbS's for now.

- **Identity.** Displays are stored as serialized `DisplayIdentity` (device path, then EDID,
  then GDI name). With nothing saved, the virtual monitor is the display with EDID `MTT-1337`
  (then any indirect-virtual output) and the glasses are a display whose EDID is in
  `KnownGlasses` (`TCL-03D4` for the GT Max). The session resolves the GDI name at each use and
  opens the DXGI output by that name (`Displays/DxgiOutputs`).
- **When it shows.** `AppController` re-reads the layout on every debounced display change
  (signature compare) and runs a session while: not paused; virtual monitor present; glasses
  present and different from it; glasses in 3D (side-by-side, even width), or in 2D with
  "Also show in 2D" (default on). Top-bottom modes are refused. The 2D/3D switch is followed
  inside the running session (`WM_DISPLAYCHANGE`, resize), not by a restart.
- **Errors.** A failed session is retried after 5 s, three times, then waits for a layout or
  settings change. A missing virtual monitor raises one tray balloon pointing at Device
  Manager and Display settings (the app never installs the driver or changes the layout).
- **Stage A guards**, only while a session runs:
  `Input/CursorGuard` — low-level mouse hook on its own thread; a move onto the glasses is
  dropped and the cursor put just outside the nearest edge that touches another monitor.
  `Input/StrayWindowGuard` — WinEvent hooks (show, move/size end, location change, restore) and
  a sweep on start and on each layout change; a top-level window whose centre is on the glasses
  moves to the same offset in the virtual monitor's work area (maximized: restore, move,
  maximize). Skips own windows, cloaked windows, the taskbars, Progman/WorkerW; at most three
  moves a minute per window. Elevated windows cannot be moved (UIPI); logged.
- **Tray**: status, Pause, Capture (Desktop Duplication / WGC), Settings, Open log, Exit.
  **Shortcuts** (low-level hook, editable): Ctrl+Alt+B pause, Ctrl+Alt+F1 settings,
  Ctrl+Alt+C exit (the DesktopSbS keys).
- **Settings window** (Windows 11 theme): virtual monitor and glasses pickers (Automatic
  first), capture method, show in 2D, cursor and window guards, Start with Windows (HKCU Run),
  shortcuts; live status and session measurements.
- Settings file `%LOCALAPPDATA%\ImmersiveSBS\settings.json`; single instance per session.

### First run (2026-10-07, started by the user)

- Detection worked: Acer `ACR-0E9F` (HDMI), panel `BOE-0A6D`, virtual monitor "VDD by MTT"
  `MTT-1337` at 2572,0, glasses "SmartGlasses" `TCL-03D4` in 3D at **3840×1080 @ 60 Hz**,
  0,-1080. The glasses' 3D refresh is 60 Hz, the same as the virtual monitor. The VDD monitor
  reports output technology HDMI, not indirect virtual, so the EDID key is what finds it.
- Every session failed in `Renderer` at `CreateBlendState`: E_INVALIDARG for the XOR-cursor
  blend. Vortice's two-argument `BlendDescription` copies the colour factors to alpha, and
  `*_COLOR` factors are invalid for alpha. Fixed (alpha Zero/One). The retry logic behaved as
  designed: four attempts 5 s apart, then "Stopped after repeated errors".
- The settings pickers showed the record's `ToString()`: the themed ComboBox ignores
  `DisplayMemberPath`. Fixed.

### Second run (2026-10-07)

The virtual monitor showed in both eyes. First numbers (Desktop Duplication, 3D, glasses
60.00 Hz measured from frame statistics): capture→Present 0.6–3.8 ms average, capture→vblank
14–20 ms average (max ~36 ms), VRAM 62–73 MB for the process. Up to ~80 captured fps with motion.

- **UAC:** Desktop Duplication loses access when the secure desktop opens
  (`DuplicateOutput1` → E_ACCESSDENIED) and reopens on its own when it closes (10 s later in the
  log). The glasses keep the last frame meanwhile, so the prompt cannot be answered through
  them. No capture API sees the secure desktop. The user's option is the UAC setting
  "Notify me only when apps try to make changes (do not dim my desktop)", which shows the prompt
  on the normal desktop; that is a security trade-off for the user to decide, and the app does
  not change it. Retry logging now writes once per error instead of four times a second.
- **New main display** (virtual monitor made primary): every monitor's coordinates were
  rebased (glasses 0,-1080 → -2560,-1080). The session reacted to `WM_DISPLAYCHANGE` before
  the rebase settled and kept the old position; the controller kept the session but never told
  it. Fixed: the controller sends the settled glasses bounds on every layout evaluation
  (`Session.MoveTo`), applied on the render thread when they differ.
- **Task View:** the window guard moved the glasses' own Task View overlay
  (`XamlExplorerHostIslandWindow`, one per monitor) onto the virtual monitor, where it stuck;
  it also kept moving the Display settings "identify" number (`MonitorNumberWindow`). Fixed:
  shell classes and shell processes (explorer except File Explorer windows,
  ShellExperienceHost, StartMenuExperienceHost, SearchHost, TextInputHost, LockApp) are left
  alone.

### Out of reach when not shown (2026-10-07)

With the virtual monitor as main display and the app paused or closed, everything on it (the
taskbar, Start, new windows) was out of sight. Handled in layers:

1. **Window mode.** Whenever the app runs but the virtual monitor is not on the glasses
   (paused, glasses away, 2D with "show in 2D" off), it is shown in an ordinary resizable window
   on a real monitor (the main display unless that is the virtual monitor, else the largest;
   half its width, centred), cursor included. The user moves the real mouse onto the virtual
   monitor and watches the window; clicks reach the real windows, no input forwarding.
   `Presentation/MirrorWindow`, `SessionOptions.MirrorBounds`, `Renderer.AllowSideBySide`.
   Closing it keeps it closed until resume or the tray's "Show in a window"; setting
   `ShowInWindowWhenNotOnGlasses` (default on). The Stage A guards run only for the glasses.
2. **Exit.** Windows on the virtual monitor are moved to that real monitor
   (`StrayWindowGuard.Evacuate`; setting `MoveWindowsOffVirtualMonitorOnExit`, default on). When
   the virtual monitor is the main display, exit asks first and explains how to get the taskbar
   back.
3. **Crash or kill.** Nothing can run then. Recovery without the glasses: Win+P → "PC screen
   only", later Win+P → "Extend".
4. The settings window warns while the virtual monitor is the main display and suggests a real
   main display with "Show my taskbar on all displays" instead.

Open: the exit question is an ownerless message box; check where it appears when the virtual
monitor is main.

### Next

1. Third run: window mode (pause, close, tray toggle), exit with the virtual monitor as main,
   main display change, Task View, then the remaining P1 checks (2D/3D switch, cursor shapes,
   WGC comparison, text sharpness).
2. Check the guards on the real layout (glasses above the Acer): cursor stopping at the top
   edge, windows opened or snapped onto the glasses coming back.
3. A proper icon.

## P4 — AI 3D mode (built 2026-10-07, not yet seen in the glasses)

The plan's V3–V6 on the captured virtual monitor (`docs/video-3d-plan.md` §4–§7). Off by
default; tray "AI 3D" (Off / Video only / Whole screen), **Ctrl+Alt+D** toggles, settings card
"AI 3D". Only in side-by-side; the mirror window and 2D mode stay flat.

**Models.** The V0 static-shape fp16 exports were copied out of the temporary V0 scratch folder
to `%LOCALAPPDATA%\ImmersiveSBS\models\` as `{da2-small|dad-base}_{H}x{W}.onnx` (224×392,
294×518, 420×756; ~740 MB, not in the repo). Packaging (P6) has to ship or download them.

**Inference** (`Depth/TensorRtRtx`, `DepthEstimator`, `DepthService`):
- ONNX Runtime 1.30 (NuGet) with the TensorRT for RTX plug-in, found through the package
  manager (`MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP.*`, 2.30.49.0 here) and registered
  with `RegisterExecutionProviderLibrary`; no DirectML fallback (plan D4).
- First use compiles an EPContext model with the engine embedded:
  `%LOCALAPPDATA%\ImmersiveSBS\engines\{stem}.trtrtx-{version}.{vendor-device}.ctx.onnx`
  (Base 518×294: 8.4 s compile). The provider's `nv_runtime_cache_path` keeps the JIT kernels:
  later loads take **1.4 s** (8–10 s without it). A stale engine is rebuilt once.
- Measured from .NET (test `DepthEstimatorTests`): Base 518×294 **8.5–9.2 ms** per frame warm,
  as in V0 (8.2 ms mean).
- One depth thread, newest input only; post-processing there (`DepthPostProcessor`, §6.1–6.2:
  sampled p2/p98, EMA τ 0.5 s, per-pixel motion-adaptive blend α_min 0.2 at 30 fps, scene cut
  at mean |ΔY| > 0.12). The service is owned by the controller and shared by sessions; it is
  disposed (GPU memory freed) when AI 3D is off. (Changed 2026-10-07 at the user's request:
  the model now loads at app start and stays loaded, idle, while AI 3D is off, so 3D and the
  theater start at once.)

**GPU** (`Depth/StereoSynth`, compute shaders on the render device):
preprocess (crop, 4-tap box, ImageNet normalisation, planar NCHW, ~1.8 MB read back); 8×8 luma
grid; joint bilateral 5×5 upsample guided by luma (§6.3); horizontal max-filter dilation 3 px
(§6.4); forward splat with `InterlockedMax` on (nearness ≪ 16 | source x 12.4 fixed + 2) at
floor and ceil per eye; resolve with holes continuing the farther neighbour; floating window
bands (§7.3.5); native split for SbS/TB video (§7.4). The renderer composites the eyes texture
over the region in each eye; the cursor stays on top at zero depth.

**Driver** (`Depth/AiStereo`, per session, render thread): luma grid → `RegionTracker` (§5.3–5.4,
energy decay 0.9, threshold 8, close, largest component, area ≥ 4% and ≥ 320×180, aspect 1–2.5,
fill ≥ 0.6, accept after 0.5 s stable, paused after 3 s still) → `Native3DDetector` (NCC of the
halves at ±3% offset > 0.85 for 2 s, out below 0.7 for 2 s) → model input → depth service →
upload → synthesise only when the frame, the depth, the bands or the settings changed. The
picture never waits for depth. Disparity (`StereoMath`, §7.1): B = 1° × ppd from the glasses'
FOV in `KnownGlasses` (GT Max 59° → 36.6 px), S = σ·min(B/(1−c), B/c), defaults σ 0.6, c 0.35;
δw = 0 because the virtual monitor fills the eye.

**Tests** (`tests/ImmersiveSBS.Tests`, `dotnet test ImmersiveSBS.slnx`, 46): stereo maths
(budget, plan table), post-processor (normalisation, breathing, motion, scene cut), region
tracker, native 3D detector, display logic and shortcuts, the warp invariants of §7.5 on a real
D3D11 device (flat at n ≡ c, opposite shifts, no holes, nearer wins, mirror symmetry, native
split, floating window), and the TensorRT model itself (skips itself without GPU or files).

Not done yet from the plan: static overlays at the screen plane (§6.5; needs tuning on real
subtitles), edge refinement of the video rectangle at full resolution (§5.3 step 5; the
rectangle is on the 8 px grid), the letterbox trim inside a tracked region (§5.5; bars carry no
motion and mostly fall outside), manual pick/lock (§5.6), Auto preset (§4.2), hardware optical
flow for 60 fps (§4.2a.5), zero-copy GPU input (§4.2a.7). Readbacks (grid, tensor) wait for the
GPU each frame; measure before optimising.

### Next

1. See AI 3D in the glasses: a YouTube video windowed and full screen, Whole screen, a
   side-by-side 3D video; tune σ, c, the tracker thresholds and the dilation.
2. Measure the added GPU time and latency with AI 3D on (the stats line shows the depth timing).
3. The §6.5 static overlays, once the viewing shows how much subtitles suffer.

## P5 — Buffered video and sound, video stretch (built 2026-10-07, not yet seen in the glasses)

Settings card "Video" and tray submenus "Video buffer" (Off / 0.5 / 1 / 2 s, default Off: the
plan's 1 s default would also delay calls and live streams) and "Video stretch"; "Sound offset"
−80…+80 ms in the settings.

**Plan open question 4 answered** (`AudioLoopbackTests`, Windows 11 build 26300): process
loopback is taken after the session mute (a muted session captures RMS 0) and after the
session volume, which acts as amplitude v²: 0.1 → 0.0097, 0.03 → 0.00087, 0.01 → 0.0000966,
0.001 → 0.0000010 of the tone; 0.0001 is silent. The control without mute or volume captures the
tone exactly (RMS 0.00342 against 0.00354). So D5 now turns the app's sessions down to v = 0.01
(−80 dB) instead of muting, and boosts the capture by (1/v)² = 10 000 (float mixer, lossless).
A boosted packet peaking above 4.0 came from a session not yet turned down (a new tab, caught
within 250 ms) and passes at unity gain; output is clamped to ±1. Not yet known: whether loopback
is taken before or after the endpoint (device) volume; if after, the delayed sound is quieter by
the device volume and needs a correction.

**Sound** (`Audio/`): `AudioDelayLine` (pure, tested) files each captured packet by its QPC time,
gaps are silence, jitter within 5 ms is absorbed and larger slips resynchronise; playback reads
the samples captured exactly the delay before the time they will be heard (now + output
latency − offset). `AudioDelay` uses NAudio 3.1 (MIT): `WasapiRecorderBuilder.WithProcessLoopback`
(process tree included: browsers play from a child process), `WasapiPlayer` shared mode, 40 ms,
on the default output at the device mix rate, at the app's own session volume.
`SessionAttenuator` turns the sessions of the process tree down on every output, restores each
one's own volume, and writes `attenuated-sessions.txt` so the next start repairs a crash.
`ProcessTree` walks the Toolhelp snapshot.

**Video** (`Depth/VideoDelayBuffer`, `AiStereo`): only the video region is delayed, never the
desktop, so typing and the cursor stay live. Region crops go into a GPU ring stamped with the
capture time (≤ 600 MB; when that cannot hold the delay at the frame rate, fewer frames are kept
rather than a shorter delay); depth for each frame is filed with it when it arrives; the frame
shown is the newest captured at or before now + 20 ms − delay, with nearness averaged over its
neighbours (weights 1-2-3-2-1, look-ahead the delay makes possible). Until the ring covers the
delay the oldest frame is held, as the sound starts silent. A frame without depth near it is
shown flat.

**Coupling:** the session reports the region to delay (buffer set, AI 3D following a video, not
native 3D); the controller finds the app under the region's centre on the virtual monitor
(`WindowFromPoint`), starts the delayed sound for its process tree, and only then tells the
session to delay the picture (`Session.AudioDelayed`). If the sound cannot be delayed, the
picture stays live, so the two cannot drift apart. A failed app is not retried until the video
moves to another app or the buffer changes.

**Stretch:** the region's overlay is drawn `VideoStretch` times wider around its centre (into
pillarbox bars); with AI 3D off the tracker still runs and the region is drawn flat; with depth
the strength is divided by the stretch so disparity stays within the budget.

Tests: 57 (+ delay line 5, loopback 6). One unreproduced failure on the first run after a build
(five reruns clean); watch for a flaky test.

### Next

1. Try the buffer with a YouTube video in AI 3D video mode: lip-sync (adjust Sound offset),
   loudness against the original, a new tab starting sound meanwhile, pause/seek, exit and crash
   restore of the browser's volume.
2. Endpoint volume question above.
3. ~~A live tuning panel~~ (done below).

## Tuning panel, hotkeys, static overlays, pick and lock (built 2026-10-07, not yet seen)

- **Tuning panel** (`TuningWindow`, tray "Tuning...", **Ctrl+Alt+T**): sliders for depth σ,
  screen plane c, edge dilation, edge softness (guided-upsample σ), responsiveness (α_min),
  scene-cut threshold, video stretch, sound offset; floating window and static overlays
  check boxes; live status and stats. Changes go through `AppController.PreviewSettings`
  (session and depth thread only, no display re-evaluation, not saved); Save writes the
  settings, Cancel or closing restores the saved ones, Reset restores the plan's defaults.
  Fine controls live in `StereoTuning` (stored as `Tuning` in settings.json).
- **Hotkeys** (all editable; the settings window now lists every command): Ctrl+Alt+X / W more /
  less depth (±0.1, DesktopSbS's keys), Ctrl+Alt+PageDown / PageUp picture back / forward
  (c ∓ 0.05), Ctrl+Alt+Shift+D pick or release the video. Hotkey changes save at once and show
  in the panel ("Last hotkey change").
- **Static overlays** (§6.5, `DepthPostProcessor.StaticOverlays`, off by default, panel check
  box): a model-resolution pixel unchanged (|ΔY| < 1.5/255) for 1 s on a strong edge (luma step
  > 0.15 to a neighbour) while the picture moves (smoothed mean |ΔY| > 0.008) ramps to the
  screen plane over 0.3 s; the temporal state keeps the model's value so switching off restores
  depth at once. Tests: subtitle stripes go to c, a smooth static patch keeps its depth, a paused
  picture is untouched. Off by default because a locked-off shot's textured static background
  could be flattened too; tune on real subtitles.
- **Pick and lock** (§5.6, `RegionTracker.Pick`/`Unlock`): the changing component under the
  cursor (searching 4 cells around it) becomes the region and automatic detection stops
  replacing it; the status says "locked". The plan's 1 s outline in both eyes is not done.

Tests: 64.

### Bigger models (2026-10-07)

Added to `%LOCALAPPDATA%\ImmersiveSBS\models` and to the settings' model list (`DepthTier`):
- **Distill-Any-Depth Large** (MIT), from the V0 scratch: `dad-large_518x518.onnx`, the only
  size its export accepts (a 16:9 frame is squeezed in; the preset is ignored). Its output is
  [1, 1, H, W]; the estimator now takes the output shape from the model.
- **Depth Anything V2 Base and Large** (CC-BY-NC-4.0: personal use, never shipped), fp16 from
  `onnx-community/depth-anything-v2-{base,large}` (`onnx/model_fp16.onnx`, float32 I/O, dynamic
  H×W), made static at 224×392, 294×518 and 420×756 with V0's `fix_static.py`. The dynamic
  originals are kept in `models\download\` for other sizes.

Warm time per frame from .NET on TensorRT for RTX (Balanced, `DepthEstimatorTests`, engines now
cached): V2 Small 6.9 ms, DAD Base 8.2, V2 Base 8.7, V2 Large 26.0, DAD Large (518×518) 41.1.
V2 Large fits 30 fps video; DAD Large fits 24 fps, and only the buffered path keeps up with it.

### Buffer: first try in the glasses (2026-10-07)

- **No sound:** the delayed copy played on the default output (`EDB323QU`, the Acer's HDMI
  audio) while the browser plays elsewhere (per-app routing). Fixed: `SessionAttenuator` records
  the output of the app's first session and `AudioDelay` plays there.
- **Intermittent pauses:** the stats show 10-second windows where the virtual monitor delivered
  no frames at all, so the browser stopped painting the video. Suspected: Chrome's player
  stalling while its audio is captured or turned down (Chrome syncs video to its audio clock).
  Not confirmed. `AudioDelay` now logs every 10 s what it captured (silent or not, packets from
  sessions not yet turned down) and played, to tell an app stall from a capture or playback one.

## Theater pop-out (built 2026-10-07, not yet seen)

The user preferred a pop-out to a built-in player: the browser's video fills both eyes, with
on-screen buttons rather than hotkeys.

- **Entering:** a "3D theater" button at the found video's top-right corner while the mouse is
  over it (fades 2.5 s after the mouse stops), or tray "3D theater". The video is found even
  with AI 3D off (`StereoSettings.FindVideo`, setting `OfferTheater`, default on): the tracker
  runs on the luma grid and nothing is drawn over the video.
- **Theater:** the region's eye images (AI 3D, native 3D, or flat with AI off; delayed when the
  buffer runs) fitted to the whole eye (`TheaterGeometry.Fit`), the desktop dark; the cursor is
  mapped from the video region into that view; **Play / Pause** and **Exit theater** at the
  top-right while the mouse moves. Play/Pause presses the media key (browsers obey it). Theater
  ends by itself when no video has been found for 4 s.
- **Buttons exist only in the glasses**, so `TheaterInput` (a low-level mouse hook on its own
  thread, only while the glasses session runs) swallows the press and release on a button,
  whose desktop rectangle the render thread publishes each frame (`TheaterButtons`); all other
  clicks pass through. Button images are drawn once with System.Drawing (`ButtonImages`); the
  renderer draws a `UiLayer` in eye px on top.
- The cursor's hot spot is now kept from Desktop Duplication (`PointerState.HotX/HotY`).

The browser keeps playing: it is the source of the picture. Tests: 75 (theater geometry, hit
testing, button images).

### First look in the glasses (2026-10-07)

- **Theater with AI 3D off was flat;** the user wants the theater always in 3D. The theater now
  converts its video whatever the AI 3D setting (`AppController.EffectiveAi3D`: Video while in
  the theater; the saved setting is untouched, so leaving turns it back), loading the model on
  entry. Until depth is there (model loading, buffer filling) the video is shown flat in the
  theater instead of returning null, which had ended the theater after 4 s.
- **Still no sound with the buffer.** The log showed two failures:
  - Chrome keeps an idle session on every output it ever used (glasses, Realtek speakers,
    EDB323QU), and "the first session" picked the wrong one: 20 min of sound played on EDB323QU
    (`played 10.0 s` per 10 s) while the user listened elsewhere.
  - When the player did open on the glasses' output, it never played (`played 0.0 s`): that
    output resets when the glasses switch 2D/3D, and the stopped player was not noticed.

  Now the output is that of the app's *active* session (the default output if several), or the
  default output with automatic stream routing while none is active; the delayed sound
  restarts when the app moves to another output for 1 s or when the player stops
  (`PlaybackStopped`), and a failed start is retried after 5 s. The 10 s log line gives the
  peak level (dBFS) and the output, and Windows' own meter of this app's session (level,
  session volume, output): proof of whether the copy reaches the speakers.
- A session restored while its output was gone stayed at 1 % for good (the marker was deleted
  anyway; the log shows Chrome found at 0.01 later). Unreachable sessions now stay in the marker
  for the next start, and a session found at exactly 0.01 is taken to be ours, original volume 1.
- **Depth crash after changing model resolution:** `TryTake` recycled the session's old
  `DepthFrame` (previous size) into the new service ("Buffers must match the processor's
  size"); a frame of another size is no longer reused.
- **Model status** (user request): the model loads at app start whatever AI 3D is set to, and
  reloads only when its file changes (Large ignores the preset). Its state (`DepthServiceState`:
  Loading, Preparing = first-use engine build, Ready, Failed) shows in Settings > Status
  (spinner, then a check or a warning, with the seconds so far), in the tray (AI 3D submenu,
  tooltip) and, while 3D is wanted, as a badge at the video's top-left corner in the glasses
  (12 pre-drawn spinner turns; "3D ready" for 2 s after loading; a warning if failed or
  missing). Tests: 76.
- **Exit sweep** (the user suspected exit left GPU memory behind; at the time the app was not
  running and the 2.2 GB in Task Manager belonged to NVIDIA Overlay). Found and fixed: the
  audio recorder was stopped but never disposed; `DepthService.Dispose` freed the model after a
  2 s join even when the worker was still running (mid-load or mid-compile), which can crash at
  exit and leave a crashed process held by error reporting; exit waited on the render thread
  without a limit (now 3 s). Exit now logs "Depth: unloaded …" and "App: exit complete", and a
  background watchdog ends the process, with a log line, if anything still holds it 3 s later.
  Windows' leak detector filed RADAR_PRE_LEAK_64 for the app once (18:44, 90 s after a start,
  during a model load); the stats line now carries private and managed memory to tell.
  Tests had been writing to the app's log (model loads after an exit were test runs); they
  write `ImmersiveSBS.tests.log` now. The buffer was already off by default (0) and in the
  user's settings.
- V2 Base 756×420 averages 825 ms per frame on this GPU (V2 Large 756×420: 67 ms): its compiled
  engine is suspect (compiled in 6.3 s, loaded in 16.7 s).
- One stats window showed V2 Base 756×420 at 907 ms per frame (1 depth frame/s); not
  reproduced or explained yet.

## Picture quality pass (2026-10-08, not yet seen in the glasses)

The user saw a shadow outline around people and hair, glitchy captions and, at times, a very
glitchy picture.

- **"Very glitchy" was a slow GPU, not a bad model.** The log averaged V2 Base 756×420 at
  825 ms a frame; tested alone its cached engine runs at 19.9 ms (all models now have a speed
  test at Balanced and Quality, `DepthEstimatorTests`, so a bad engine fails it). The GPU was
  slow at the time (memory pressure, battery or load). The live path applied that second-old
  depth to new frames, which tears edges in motion: now depth more than 250 ms behind a moving
  picture shows the video flat until it is back under 150 ms (`AiStereo.DepthStaleMs`), with a
  log line each way. The stats line gives VRAM against the process's budget.
- **The outline was the hole fill.** A hole beside a foreground edge continued reading the source
  past the background neighbour, i.e. the object's own edge pixels: a second outline. Holes now
  mirror the background away from the edge, through a 3×3 tent blur. The splat's sub-pixel
  sample point no longer reaches across a depth edge (a 1 px ghost), and a pixel only writes
  columns it really covers.
- **Edge snapping** (new `Snap` pass, full resolution, horizontal radius ≈ 2.5 model px): where
  the nearness spans a depth edge, a pixel takes the near or far extreme by which side's colour
  it resembles; a pixel like neither (hair) blends. The guided upsample is guided by colour, not
  luma. Dilation default 3 → 1 px (wider drags a background sliver along: a halo). Tuning panel:
  "Snap depth edges" (on).
- **Captions** (`CaptionLock`, model resolution, CPU): rows in the lower 45 % with ≥ 3 short
  bright strokes each beside a much darker pixel (outline or shadow), grouped into a wide, short
  band, are given one depth: the band's 90th-percentile nearness (at least the screen plane)
  + 0.03, smoothed 0.3 s; the mask rises in 0.1 s and fades over 0.4 s. Tuning panel: "Keep
  subtitles flat" (on). The old §6.5 static overlays stay for logos.
- **Depth Anything 3 Small and Base** (Apache-2.0, `onnx-community/depth-anything-v3-*`): an
  any-view export (input [B, N, 3, H, W]; outputs depth, confidence, camera). Cut to one image
  and the depth output (the camera head goes), static at the three sizes, kept **fp32**: the
  fp16 converter mistypes the graph's casts (rotary embedding, then GELU constants). Their output
  is depth, inverted in `DepthEstimator` (`DepthModel.OutputIsDepth`). Time per frame: Small
  15 ms Balanced / 46 ms Quality; Base 36 / 109. The originals are in `models\download\da3-*`;
  the converter script lives in the session scratchpad (not the repo).
- **DA3 in fp16** (2026-10-09): the converter failed on the dynamic export's shape arithmetic
  (Shape/Gather/Range and the casts between them, rotary tables included). Folding it into
  constants first (ORT's basic optimizer on the static fp32 model; standard ops only) leaves a
  graph the fp16 converter handles; checked against fp32 on ORT CPU (mean error 0.015 %, max
  0.07 %), and the speed test's near/far check passes. Time per frame now: Small 7.5 ms
  Balanced / 10.4 Quality; Base 11.9 / 19.2 (2–5.7× faster). Files 40–45 % smaller; the fp32
  ones are kept in `models\download\da3-*\fp32`. Engine cache names now carry a fingerprint
  of the model file (size, time written): replaced under the same name, a model kept running
  its old engine. Every model's engine is rebuilt once (about 20 s on first use) and the old
  one removed.
- **Video Depth Anything Small** (Apache-2.0) exists as an ONNX pair (per-frame encoder at
  518×910 + a 32-frame temporal head); the head needs the last 32 frames' encoder features
  (~15 MB each) per call, so it needs GPU-resident inputs (ORT IOBinding), not the CPU arrays
  the estimator uses now. Not done.

Tests: 91 (hole fill from the background, edge snapping, five caption cases, all models at two
sizes).

## Theater: steadier, mouse kept on the video, settings panel (2026-10-08)

The user saw the theater "glitch in and out" and the mouse "go behind it".
- **Flat/3D flicker** (the main cause): the stale-depth guard measured wall time since the depth's
  frame, which grows while the picture is still; the first frame after any still moment looked
  seconds behind and flipped the video flat for ~100 ms (71 times in one session, including on
  entering the theater). Now measured in frames handed to the model since the depth's frame
  (≥ 6 frames and ≥ 250 ms of the video's time to go flat, ≤ 3 frames to come back).
- **The theater followed the tracker**, so it jumped or dropped to the desktop when the player's
  controls appeared or the video paused. It now keeps the area it was entered on until exit
  (`AiStereo` theater region; every new frame counts as moving there). It no longer ends by
  itself when the video goes; Exit does it.
- **Mouse**: only the video shows in the theater, but the cursor roamed the whole virtual
  monitor and clicked unseen windows. While the theater shows, the cursor is confined to the
  video's desktop rectangle (`ClipCursor`, re-applied every 250 ms, freed on exit, pause and
  app exit).
- **3D settings panel** in the theater: a "3D settings" button beside Play/Pause and Exit opens
  rows of − / + (model, cycling through the models whose files exist at the current resolution;
  resolution; depth; screen plane; buffer Off/0.5/1/2 s) and two switches (edge snapping, flat
  subtitles). Changes apply and save at once, like the hotkeys. Clicks anywhere on the panel are
  caught; it closes after 10 s without mouse movement.

Tests: 93 (panel layout, panel labels). The model tests' speed limits were widened (100 ms
Balanced, 250 ms Quality): run while the app uses the GPU, times rise 1.5–2×.

**Seen in the glasses (old build, 22:35):** the hair improved a lot; still glitchy at times (the
wall-time flicker above, 80 flips in that run) and the picture "often split in half, top and
bottom not matching". Not tearing (the capture copies before releasing, presents wait for
vsync) and not native-3D detection (none logged): the first `CaptionLock` joined every band of
text-like rows in the lower 45 %, and a busy scene there (foliage, patterns) became one block
flattened to one depth, a seam across the picture. Now: strokes need a dark outline on both
sides and ≥ 4 per row; only the lowest band, plus a line right above it, at most 1/8 of the
height per line and 1/5 in all, centred; the band must hold still for 0.3 s against where it
first appeared; "Depth: subtitles found at …" / "gone" in the log. Tests: 96 (busy lower
picture, texture above a subtitle, a drifting band).

## Buffer: no more freezes, and a bug and performance pass (2026-10-09)

The user found the buffer unusable: poor sound, and constant pauses and lag. Their guess, a
"transition between recordings", was close: there are no recordings, but both delay lines were
being reset or jumped.
- **Picture froze for the whole delay** whenever the found region changed by a pixel (the
  player's controls appearing, the tracker refining its box): `SetRegion` emptied
  `VideoDelayBuffer`, which then held its first frame until it covered the delay again. Each
  held frame now keeps its own crop and is shown where it was; depth is smoothed only over
  frames cut the same way.
- **Sound restarted** whenever the region went missing for a moment (silence for the delay,
  after the live sound jumped ahead): the controller now keeps the delayed sound and picture
  running through a 5 s gap.
- **Clicks**: `AudioDelayLine` resynchronised whenever the render callback's clock reading was
  more than 5 ms off (13–17 times a session, each a jump). It now reads contiguously and follows
  the smoothed error a frame at a time; only a jump over 40 ms resynchronises, and a lip-sync
  trim change applies at once. The `Audio:` line shows the frame corrections.
- **Render thread no longer waits for the GPU in front of a present.** The luma grid and the
  model input (up to 3.8 MB of floats) were read back at once, twice a frame, between the swap
  chain's wait and the present, on a GPU shared with the depth model. They are now queued while
  the frame is built and read after the present (`AiStereo.CollectReadbacks`); the tracker
  follows one frame later, which nobody sees.
- **Large-object-heap churn:** the luma grid read allocated a 130 KB array per captured frame,
  and `CaptionLock` a list the size of the caption box per depth frame (up to 250 KB): Gen 2
  collections and hitches. Both reuse buffers now.
- **Depth thread:** the depth range's percentiles sorted a quarter of the map (38k–79k values)
  per depth frame, in series with the model; `CaptionLock` sorted its box. Both use quickselect
  (`Depth/Selection`), linear and in place.
- GPU buffers make their staging copy only when read back (the splat's key buffer, 16 MB at
  1080p, never is).
- **Start-up crash, made and fixed in this pass:** a supposed fix (IsIconic "imported from
  shcore.dll", a misread listing; it never was) was a search-and-replace that moved
  `GetDpiForMonitor`, which is in shcore.dll, to user32.dll, and the app stopped at start.
  Restored; `NativeImportTests` now resolves every native import against its DLL, so a wrong
  DLL fails a test instead of the app.

Tests: 109 (delay line jitter, catch-up and restart; held frames across a region change;
quickselect against a sort; every native import resolves).

## Browser extension for the buffer (2026-10-09, branch `browser-extension`)

- **Browser extension** (`extension/`, Chrome and Edge, MV3): the page script routes every
  video and audio element through Web Audio, element → `DelayNode` → output, when the app asks.
  The sound never leaves the browser, so there is no capture, no turning down, no boost and no
  clock to follow, and a pause or seek stays in step by itself. The app runs a WebSocket server
  on 127.0.0.1:47391 (`Browser/BrowserLink`) that accepts only the extension's origin (its ID,
  `dopaoaefmhhmpbdgpnkgpkjhbndebmnl`, is fixed by the public key in the manifest; the private
  key is not needed for an unpacked extension and is not kept).
- It also reports where each video's picture is (CSS px in the top viewport, through nested
  frames, inside the element's letterbox) and the app places it on the desktop: Chromium puts
  the page at the bottom-left of its client area, so the viewport's size × devicePixelRatio
  places it. That exact box replaces the tracker's while the two agree, so the region no longer
  moves with the player's controls; seeks clear the depth history; the theater's Play/Pause
  plays or pauses that very element.
- **Fallback**: media the page cannot delay (DRM, another origin without CORS, or audio not yet
  allowed to start before a click) are reported, remembered, and their sound goes through
  process loopback as before. Other apps use loopback too. A loopback start that finishes after
  the page took over is dropped.
- Pages measure and report nothing while the app is not connected, and walk shadow roots every
  30 s then (5 s while connected; only shown pages). An element removed from the page while
  delayed is played live again first.
- Install: Settings → Buffer → "Open extension folder" (copied next to the exe), then
  edge://extensions or chrome://extensions → Developer mode → Load unpacked.

Not yet seen in the glasses or in a browser. Open questions for the first run: the page's
volume control and mute with the sound in Web Audio (expected to keep working, to confirm);
the lip sync (Web Audio adds its own output latency, in the `Audio:` line; the sound offset
trims it); the exact box on a normal (not maximised) window, where the client area's borders
could shift it a few px (both boxes are in the log).

Tests: 119 (the link's handshake, origin check and messages; placing and matching a page's
video).

### Next

1. In the glasses: the buffer with the extension (sound quality, no freezes; the `Audio:` and
   `Browser:` lines), the theater (enter, exit, Play/Pause, cursor alignment), stretch, tuning
   panel; the outline, captions and edge snapping (A/B in the tuning panel); compare the models,
   DA3 included. The user's saved tuning still has dilation 3 (Reset in the tuning panel gives 1).
2. Video Depth Anything with IOBinding.
3. P6 packaging (installer with the driver, model download, README), or P3 once the hardware
   3D button test with the glasses removed from the desktop is done.

## Improvement plan, phases 0–5 (2026-10-10, not yet seen in the glasses)

`docs/improvement-plan.md`, implemented in its order. Every new method sits beside the old one
and can be switched in the tuning panel; the measures below are in the `Stats:` line.

- **Phase 0, measures.** `Presentation/GpuTimer` times every GPU pass (M5: timestamp queries,
  read a few frames later, never waited on); `Resolve` counts hole pixels (M2, side edges
  excluded); the post-processor reports flicker on still pixels (M4); `AiStereo` samples the
  depth age of every frame shown (M1, mean and p95) and counts hand-overs, skipped repeats and
  the model's busy share (M7). The depth status splits model, post and waiting time (M6).
  Two lines join the `Stats:` line: `Measures: …` and `GPU ms per frame: …`.
- **Phase 4 step 0, only changed frames to the model.** Desktop Duplication's dirty and move
  rectangles say whether the video's area changed; the luma grid checks after the present
  (and decides with Windows.Graphics.Capture), drops exact repeats, and after 3 misses within
  10 s the dirty rectangles are no longer trusted (logged). While the page says the video is
  paused, nothing is handed over (unless the picture keeps changing for 0.5 s). The buffer and
  matched timing hold only changed frames too.
- **Phase 2, edges.** `Depth/EdgeFilters`: soft edges (k passes across from §7.1's formula,
  one down) and depth anti-aliasing (structure tensor, 5-tap folded Gaussian along the edge)
  on the output only, before the overlays and captions. New defaults (settings version 2
  migrates saved files): screen dilation 0, edge snapping off, soft edges Auto, anti-aliasing
  Auto (on for DA2 and DA3). **Added beyond the plan:** a screen-resolution slope limit
  (`SlopeLimit` shader, n′(x) = max_d n(x+d) − g|d|, g = 1.9/S), because for a step of 0.8 soft
  edges alone still left 0.08–0.09 % holes. Measured on a 1920×1080 test scene at σ 0.6:
  holes 0.13 % (Δn 0.4) and 0.34 % (Δn 0.8) with the old chain, 0 with the new one; edge
  straightness (M3) 0.07–0.09 px RMS on staircase edges. Anti-aliasing thresholds are
  smoothstep(0.5, 0.8) instead of the plan's (0.7, 0.9): a gentle staircase only reaches
  coherence 0.8, and the plan's values halved its effect (slope ⅓: 0.274 → 0.165 px; now 0.081).
- **Phase 1, matched timing.** A timing setting: Live, Auto (default; matched once μ + 2σ + T̄
  of the depth age passes 60 ms, live below 45 ms), Matched, Buffer 0.5–2 s (settings window,
  tray, theater panel). `MatchedDelay` sets the delay (plus 20 ms present latency, 10 ms steps
  or whole video frames, follows only after 20 ms for 2 s); held frames get their own depth or
  depth interpolated in time (video time with the extension). Up to 90 ms the sound stays live
  unless the page can delay it; beyond, the delayed-sound path runs at exactly that delay. The
  session now reports the wanted delay and the controller the sound's actual delay
  (`SoundDelay`, replacing `AudioDelayed`).
- **Phase E, extension protocol 2** (extension 1.1.0). The app follows one video; its frame
  reports each presented frame (requestVideoFrameCallback), its state at once on change and
  twice a second, the boxes over its picture (an 8×8 `elementsFromPoint` probe plus the
  player's caption containers and native `<track>` cues), and keeps an NTP-style clock offset
  to the app (best of 8 round trips, every 5 s). App side: `PageVideo` (thread-safe, read by the
  render thread), `BrowserLink.Follow`, boxes mapped to the screen by the controller. Uses:
  media times for matched interpolation and frame-exact delays, pause and seek, overlays held
  at the screen plane (§7.4 mask, rise 0.1 s, fade 0.4 s) and the page's captions instead of the
  detection. Trust rules (§3.5): the tracker must disagree for 1 s before its box wins again,
  its search runs at 4 Hz while the page's box is used, page data older than 1 s is ignored.
  **Deviation:** the change test for hand-overs stays with the capture (dirty rectangles are
  exact per captured frame; frame events arrive on their own schedule), the page's frame events
  stamp media times.
- **Phase 6.1, subtitles.** Band narrowed to the middle half (the logged false box at centre 413
  of 518 is outside); a box that comes back within 5 s must hold for 1 s.
- **Phase 4 steps 1–2.** Post-processing has its own thread, alongside the next frame's model.
  A new frame's model input is queued as soon as it is captured (live timing), before waiting
  for the swap chain, and handed over once the GPU has copied it.
- **Phase 3, lookup warp.** `Lookup` shader (smallest/largest root, linear refinement, each
  side's own root at sharp edges, farther sample in gaps); Warp setting in the tuning panel,
  Splat by default until compared in the glasses. GPU time at 1920×1080, σ 0.6, kept busy:
  splat 0.90 ms, lookup 0.84 ms. The plan's span skipping is there (exact) but off: with a
  17 px search it cost more (1.09 ms) than it saved.
- **Phase 5.1, look-ahead in the buffer.** The depth thread holds K frames (as many as fit in
  the delay less 0.3 s, at most 30) and normalises each toward the range of those after it, up
  to the next scene cut; held frames go after 200 ms without new ones.
- **Phase 6.4, foreground scale.** `Depth/ForegroundCurve`, a softplus curve on the output
  (k = 8, b = 0.15·L): "Foreground" in the tuning panel, −3..3, 0 by default. At +1/+2/+3 the
  nearest part's slope is 1.22/1.44/1.80 and the farthest's 0.28/0.12/0.05; negative levels are
  the inverse curves. It works in output nearness, so the screen plane (c) sits at a different
  place in the scene once it is on.
- **§14 "Use page data".** A Settings checkbox (on by default). Off: the extension still delays
  the sound, but the app stops following the video and the page's place, frames, pause, seeks
  and boxes no longer reach the picture. The way out when a page reports wrongly.
- **Phase 4 step 4, the spike's risk question.** The TensorRT for RTX packages (EP 1.8 and 2)
  ship `cudart64_12.dll`, but it does not load from outside its package; the driver's
  `nvcuda.dll` exports the whole interop path (`cuD3D11GetDevice`,
  `cuGraphicsD3D11RegisterResource`, `cuGraphicsMapResources`,
  `cuGraphicsResourceGetMappedPointer_v2`). The model input is already a structured D3D11
  buffer of planar floats, so it maps to a linear device pointer with no copy. Still open: an
  ONNX Runtime IOBinding to that pointer, in the EP's CUDA context (the primary context). The
  step itself waits until the glasses' numbers show the depth path is the bottleneck.

**Measured, and to remember:** a Debug build runs the depth post-processing 5–7 times slower
(518×294 with anti-aliasing and two soft passes: 55 ms against 8 ms a frame in Release; the
app now logs a warning). Runs in the glasses must use a Release build. In Release the edge
steps add about 4–5 ms of CPU a frame (anti-aliasing ≈ 2 ms, a soft pass ≈ 1 ms), on its own
thread; moving post-processing to the GPU (Phase 4 step 3) removes it.

Tests: 188, in Debug and Release.

Not done, each waiting on something:
- Phase 4 step 3 (post-processing on the GPU): the plan does it only if post-processing shows
  in M6. It now runs alongside the model, so it costs only when post > model.
- Phase 4 step 4 (input kept on the GPU): feasible; only if steps 1–3 leave the depth path the
  bottleneck.
- Phase 5.2 (Video Depth Anything streaming): needs an export and a 3–5 session spike.
- Phase 6.2 (needs a salient-object model) and 6.3 (only if the lookup warp's stretch still
  shows).

### Next

1. In the glasses, Release build, with and without the extension, DA2 S, DAD B and DAD L: the
   `Measures:` and `GPU ms` lines (Phase 0's done-when), tearing in Auto/Matched timing,
   shadows and jagged edges with the new edge defaults, the lookup warp A/B, page captions and
   controls held flat, the `Browser: the followed video` line (fps, clock error).
2. Then decide from the numbers: matched thresholds, soft-edge passes, lookup as default.
3. Phase 4 step 3 if post-processing shows in M6.

## First glasses run of the improvement plan (2026-10-11, 20:57–21:19, Release)

WeTV in Edge with extension 1.1.0; DA3 S, DA2 S/B/L, DAD B/L, 518×294 and 756×420; Auto,
Matched and Buffer 0.5/1 s; theater on and off; splat, then lookup.

**Seen:** DA2 Small looked great; the other models tore slightly. Hair fine; a slight blur and
bend beside some edges (a wing), only on close look. The buffer glitched (pauses, missing
frames, interruptions). The user could not tell whether the extension worked, and heard the
sound only from the browser (by design: the page delays it).

**Log:**
- The extension worked throughout: protocol 2, the video followed at 30 fps, clock within
  0.5–0.85 ms, 1–11 overlay boxes, the page's sound delayed as asked.
- No holes (M2 0.00 %) with any model. Flicker 0.002–0.03.
- DA2 S/B at 518: model 9–11 ms, post 10–14 ms alongside, about 31 depth frames/s; matched
  needed 74–101 ms. DA3 S at 756: model 9–10 ms, **post 20–26 ms** (now the larger part; Phase
  4 step 3's condition is met there). DA2 L at 756: model 60–113 ms, GPU 96–99 % busy, capture
  down to 8–12 fps (too heavy for this GPU at that size).
- **The matched delay hopped** 100 ↔ 133 ↔ 167 ↔ 300 ms every 10–20 s: the target crossed a
  33 ms frame boundary, and the ±20 ms / 2 s rule followed it each time. Each move skips or
  repeats a frame, and the page bends the sound's pitch for a moment (`setTargetAtTime`).
- **The followed video was lost for one tick** (about 0.3 s) seven times, mostly around theater
  on/off. Each time the page's delay went back to live, the app captured the sound for 0.3 s,
  and the page then restarted its delay line: a gap in the sound, and the buffer restarted
  (the pauses).
- The subtitle detector found and lost the player's control bar (13,267 496×12 of 518; 710–746
  of 756 wide) every few seconds.

**Fixed:**
- The followed video is kept as last seen for 1.5 s when it is missed. `RootWindowAt` looks
  through owned popups (`GA_ROOTOWNER`), so a browser tooltip or bubble over the video no longer
  counts as another window.
- `MatchedDelay`: up after 0.5 s; down only after 10 s with the target more than a step (and
  20 ms) below, then to the target plus a step. A target wandering across one boundary is
  never followed.
- `CaptionLock`: bands wider than 85 % of the picture, or half under a page overlay box, are
  not captions.
- Extension 1.2.0: a toolbar badge ("off" red: no app, "on" grey: connected, "3D" green: the
  followed tab; hover for the sound's delay). The README says how to pin it and what each means.
- Post-processing split over bands of rows on up to 8 cores (`RowBands`; soft edges,
  anti-aliasing, the temporal pass, overlays, captions' row scan and mask, flicker). Release, in
  `EdgeFilterTests`: 756×420 with anti-aliasing and 2 soft passes 16.5 → 7–8 ms, 518×294 8 → 4 ms.
  This stands in for Phase 4 step 3 (see the plan's status).
- More of it split, and measured on a real clip (`ParallelSpeedTests`: 48 frames of Big Buck
  Bunny from `%LOCALAPPDATA%\ImmersiveSBS\samples`, decoded with `MediaComposition`, depth from
  DA3 Small on TensorRT; one core against the bands on the same frames, outputs identical):

  | ms per frame | 518×294 one core | parallel | 756×420 one core | parallel |
  |---|---|---|---|---|
  | post-processing (AA, 2 soft passes, captions) | 8.5 | 3.3 | 16.5 | 5.7 |
  | luma, depth thread (now SIMD) | 0.18 | 0.11 | 0.32 | 0.16 |
  | buffer blend of 5, render thread (now one SIMD pass) | 0.28 | 0.16 | 0.40 | 0.20 |
  | matched blend of 2, render thread | 0.07 | 0.05 | 0.14 | 0.07 |

  16 bands on at most 8 threads (twice the bands balanced the edge pixels: 2.1–2.6× → 2.6–2.9×;
  all 16 threads would give 5.1 ms but leave the render thread fewer cores). The percentiles
  now come from a histogram counted in parts and a search of the one bin (exact, as a sort).
  DA3's depth-to-inverse step and the look-ahead's change measure are split too.

- **Fewer models** (user request): Distill-Any-Depth Large (one 518×518 size squeezing 16:9,
  41 ms), Depth Anything V2 Base (non-commercial, no better than DAD Base at the same cost) and
  V2 Large (non-commercial; at 756×420 the GPU 96–99 % busy, capture 8–12 fps) are gone from
  `DepthTier`, the settings list and the theater's model buttons. Left: DA2 Small, DAD Base
  (default), DA3 Small, DA3 Base, all Apache-2.0 or MIT. A saved file naming a removed model
  loads it as DAD Base (`DepthTierConverter`); the rest of the file is kept. Their files are
  still in the models folder.
- **The models' outputs agree once converted** (`ModelAgreementTests`, 12 frames of the sample
  clip at 518×294). DA2 and DAD give inverse depth at their own scales (p2..p98 0.25..5.7 and
  0.73..12.6); DA3 gives depth (0.34..1.7), which `DepthEstimator` inverts. After the
  percentile normalisation every model orders near and far like DA2 Small (Spearman ρ: DAD B
  0.99, DA3 S 0.96, DA3 B 0.97; mean |Δn| 0.04–0.055, the models' own differences). No DA3
  pixel reaches the inversion's floor. Read like V2's output instead (depth flipped, not
  inverted), DA3's nearness would move by 0.19–0.24 on average: the "blown-out" DA3 that V2
  pipelines show.

### Sound only in the page (2026-10-11, user request)

The browser's volume still dropped to 1 % in the run: each time the followed video was missed,
the app fell back to capturing Edge's sound (`AudioDelay`, which turns the live sound down to
v = 0.01 so the capture can replace it). The standalone path is gone: `AudioDelay`,
`AudioDelayLine`, `ProcessTree`, `SessionAttenuator`, their tests and the NAudio package.

- **Only the extension delays sound.** The controller sets `Session.PageCanDelay` each tick (the
  extension follows the video and can delay its media). Without it: no buffer (a saved buffer
  runs as Auto, the status says why; the buffer choices are greyed with "needs the browser
  extension" in the tray, the settings and the theater panel's steps), and matched timing is
  capped at 90 ms with the sound live (before: dropped to live beyond 90 ms).
- **Only the followed video's tab is delayed** (extension 1.2.1, `delay` message with `tab`):
  before, every tab's sound went late with it, music elsewhere included.
- **Found while checking the logic:** whenever the page's actual delay differed from the
  buffer's (a page not yet delaying, a paused element not yet taken in), the buffer fell back to
  live and **emptied itself**, then froze for its whole length when the delay came: the
  run's "pauses". Now the picture's delay follows the sound's (`AiStereo.ShownDelay`): until
  the page delays, 90 ms with the sound live; a longer delay is reached by holding the frame
  (the page's new delay line is silent as long), a shorter one at once. In the buffer, frames
  whose neighbours have no depth yet take the nearest depth instead of showing flat.
- Not changed, noted: the page's sound goes through Web Audio, whose output latency the log
  gives as 50 ms; against the element's own path that may leave the sound some 20–40 ms late.
  Within lip-sync tolerance; Sound offset trims it.

### Theater button only on real videos (2026-10-11, user request)

The button showed "everywhere": it followed the tracker's box, and any moving area (scrolling,
animations) looks like a video to it. With a browser connected it is now offered only over the
videos the extension reports visible (at least 160×90, in the browser window under the cursor
or the followed video's, that window on top at the video's centre), at the top centre of the
one under the cursor. Pressing it opens the theater on that video: the controller follows it
at once (the tracker's box is not consulted for 2 s, nor in the theater), and `AiStereo` gives
a newly reported box its own second of trust. Without a browser, the tracker's box as before.
The "Show a 3D theater button" setting now also applies with AI 3D on (it did not).

### Privacy: no window titles; the extension's data used in every timing (2026-10-11)

- **Video names in the log came from the app, not the extension:** `StrayWindowGuard` logged
  the title of each window it moved, and a browser's title names the page ("… EP1 … | WeTV").
  It now logs the program and window class only, and `Native.GetWindowText` is gone. The 15
  such lines in the existing log were redacted. The extension sends no titles, addresses,
  sources or text (its README now lists what it sends).
- **"The extension does not work in live timing":** the log showed AI 3D running for minutes
  without following the browser's video. The controller found it only through the tracker's
  box (≥ 50 % overlap), and the tracker had another area or an offset box. Now, when that
  match fails, it follows the largest playing video the extension reports on the virtual
  monitor; a followed paused video gives way to one that plays (not in the theater or right
  after a pick); and `AiStereo` takes the page's playing video over a tracker box elsewhere
  entirely (overlap < 0.05; the one-second trust rule stays for a partly overlapping box).
- `DepthServiceTests` joined the "Timing" collection: its 200 ms idle rule failed under load.

Tests: 186, Debug (the user's Release build was running).

### Theater redone as a player window (2026-10-11, user request)

The run at 23:37–23:45 (extension 1.2.1 working: page sound 2.00 s late in the buffer,
protocol 2 at 25 fps) showed three theater problems:

- **The button over the desktop:** without a connected browser (after 23:44:40 the extension
  disconnected) the button fell back to the tracker's box, which can be any moving area. Now it
  is offered only over the videos the extension reports, and not at all without it.
- **A double click resized and cropped the page's video:** the theater was drawn only in the
  glasses, so clicks went through to the player under it; a double click turned YouTube full
  screen, and the theater kept the old box. The theater is now a real window of the app's own
  (`TheaterWindow`) over the whole virtual monitor, left out of the capture so the video is still
  captured under it. Clicks and keys land on it: Esc or a double click leave, Space/K
  play/pause, ←/→ and J/L seek 5 s (extension 1.2.2, `seek` message). It is a tool window so
  Chromium does not count the browser as covered (a covered browser stops painting), and
  `Native.RootWindowAt` looks through it, so the followed video stays followed.
- **Jumps:** in the theater the page's exact box is always trusted and followed (the tracker
  cannot be watching another video there), and kept while it is missed.

Not yet seen in the glasses: that Desktop Duplication leaves the window out (it should, as for
`WDA_EXCLUDEFROMCAPTURE` in other capture tools) and that Edge keeps painting under it.

### Theater controls; a false "3D video" broke the delayed sound (2026-10-12)

The run at 23:59–00:06 (player window in use; no crash, no error but one `AcquireNextFrame`
0x887A0026 at the mode switch, recovered) showed:

- **The delayed sound restarted every 20–40 s** ("plays the sound live again", then 0.50 s
  again ~5 s later), each time with seconds of silence. The `Stats:` lines alternate with
  "side-by-side 3D video, shown as is": `Native3DDetector` took the 2D video for side-by-side
  (whole-block correlation of the halves is high whenever brightness changes mostly from top to
  bottom), showed each half to one eye, and the native path zeroed the delay. The detector now
  correlates only detail: each row's (side-by-side) or column's (top-bottom) straight-line fit
  is taken out. Tested on drawn gradients and on Big Buck Bunny, which stays 2D while its
  squeezed side-by-side version is still recognised. A detection in Auto no longer zeroes the
  delay at once (the controller's 5 s grace applies).
- **Theater controls (user request):** play/pause left the top (3D settings and Exit stay);
  a bottom bar has a seek bar (click to jump there), the time, 10 s back, play/pause, 10 s on
  and mute, and stays while paused. Extension 1.2.3 adds `time`, `duration` and `muted` to the
  followed video's `state` message, `seek` to a time, and `mute`. The renderer now frees UI
  textures not drawn once it holds over 256 (the time label is a new image each second).

### Video found only by the extension (2026-10-12, user request)

"Remove the guessing video location": `RegionTracker` (and its tests) is gone. AI 3D "Video",
stretch and the theater use only the box of the video the extension reports; without the
extension, only Whole screen converts, and the theater says it needs the extension. The
controller no longer matches the extension's videos against a guessed box: it keeps the
followed element while it is visible and on top in its window, a paused one gives way to a
playing one (not in the theater or within 2 s of a pick), and otherwise it takes the largest
playing video it finds on the virtual monitor (windows looked up under the cursor and at nine
points). The pick shortcut (Ctrl+Alt+Shift+D) now follows the reported video under the cursor.
With page data off, the video's place is still used (only frames, state and boxes are not).
`StereoSettings.FindVideo` became `OfferTheater`: nothing runs for the button any more.

### Two or more browser windows (2026-10-12, user report: "struggles with 2+ video tabs/windows")

A page reports its video in its own viewport's px, and the app placed it in every browser
window big enough for the page (the only check): with two windows of a similar size, each
video showed up in both, so the app could follow a phantom (the wrong place converted, the
wrong tab's sound delayed, the theater's controls on the other video). Extension 1.2.4 reports
each page's window place and size from its screen's work area (`screenX − screen.availLeft`,
`outerWidth`…, DIP); the app takes, for each video, the window whose `GetWindowRect` (from its
monitor's work area, at its DPI) matches best. Tabs in one window were already told apart (a
background tab reports itself hidden). The "following a … video" log line now says how close
the window matched ("its window matches within N DIP"): that number should be 0–2 in a run;
if it is large, the DIP assumption is wrong for that setup.

The user then clarified: one video at a time, switching between two Edge windows, and the
theater should follow. The controller now watches the foreground window (not the theater's):
on a switch to a browser window with a reported video, that video (playing first, else the
largest) is followed at once, in the theater too, so the theater, the delayed sound and the
bar's controls move with it ("Browser: switched to another browser window; following …").
A video in the window the user works in is kept while paused.

### The 3D theater moves into the page (2026-10-12, user request)

The user showed Universal 3D Viewer: its button sits in the page over the video and opens a
player in the page that can go full screen. The app's own theater (a button drawn only in the
glasses from the page's report, a quarter of a second late, a player window left out of the
capture, a mouse hook catching clicks) was "too laggy and inconsistent". Now:

- Extension 1.3: the button and the player are the page's. The button shows over a video under
  the mouse while the app says the glasses are in 3D mode (`status` message, also carrying the
  3D panel's values and whether the user wants the button). The player covers the page, goes
  full screen on the click and draws the video on a canvas at its own size, so the capture
  gets it sharp, not as small as the page laid it out (1131×636 in the last run). Controls,
  keys and a 3D panel (its buttons send `command`) are its own; the page's captions are copied
  in. Opened from the tray or the shortcut (`theater` message, no click in the page), it fills
  the browser window and the worker puts the window in full screen.
- The app follows the video whose report says `theater` before anything else, turns AI 3D on
  for it and fits it to the whole eye; `TheaterUi` keeps only that and the model badge.
  Removed: `TheaterWindow`, `TheaterInput`, `TheaterButtons`, the app-drawn buttons and bar,
  `Session.PageVideos`, the cursor clip, the app's play/seek/mute messages and the page's
  time/duration/muted report.

Not yet seen: whether Chrome keeps presenting frames of a video covered by the player (if not,
the canvas falls back to drawing each animation frame, but the frame events the app times the
picture with would stop), and the player on sites whose captions are not in caption-like boxes.

### The extension becomes part of the app (2026-10-12, docs/extension-first-plan.md phases 1–3)

The user approved the plan's three decisions (browser video in 3D only through the theater;
already-3D video chosen, not detected; the stretch drawn by the player). Done:

- **Phase 1: one extension, one protocol.** Extension 1.4.0 speaks protocol 3; `BrowserLink`
  accepts only that (`BrowserLink.Protocol`). An older copy is logged, kept out of
  `Connections`, and the tray says to reload it. Removed: protocol 1 and 2 paths, delays for
  "every tab", the `UsePageData` setting, the pick shortcut. Settings files from before load: a
  saved pick shortcut is skipped (`ShortcutMapConverter`), "Auto" 3D detection loads as 2D
  (`Native3DModeConverter`), "the browser video only" loads as Off (`ForgetRemovedChoices`).
- **Phase 2: browser video means the theater.** The page reports its open theater (`theater`:
  its picture in viewport CSS px, the px ratio, whether the viewport is the screen) and, while
  open, its frames, state, boxes and clock pings without being asked. The controller follows the
  newest open theater (`TheaterReport`), when a window fills the virtual monitor
  (`FullScreenOn`), and maps its picture by the px ratio. Removed from the app: in-place
  following (`PageVideosNear` and its nine probe points, `FollowBrowserVideo`,
  `PlayingBrowserVideo`, `VideoInWindow`, foreground-window switching, grace and pick rules),
  page-to-screen geometry (`BrowserMedia.ScreenRect`/`ToScreen`/`WindowError`/`Match`,
  `Native.WindowInWorkArea`, `ClientBounds`, `ForegroundRootWindow`), `follow` messages, the
  "Video only" AI 3D target. Removed from the extension: the overlay probe (an
  `elementsFromPoint` grid over unknown players), the iframe position messages, window and
  viewport reports. The tray's theater asks every browser; each opens it on the largest playing
  video of its tab in front.
- **Phase 3: the player decides when the picture changes.** In the theater a frame goes to the
  model when the player reports a frame drawn since the last one handed over (matched to the
  capture's time; until the clock is synced, each new capture while it plays); the whole screen
  hands over each new capture. The player emits its frame events from its own draws (the
  element's frame callback, or the animation loop if that stops), so they hold even if the
  browser stops presenting the covered element. Removed: the luma grid (and its GPU pass), the
  capture's dirty-rectangle tracking, the paused-but-changing distrust, `Native3DDetector` and
  its tests, the app's stretch, the app's theater fit (`UiLayer.Theater`, the renderer's theater
  branch, `TheaterGeometry.Fit`). The player's 3D panel gained "Stretch" and "Video: 2D /
  side-by-side / top-bottom"; the status carries both.
- Phase 4 (packaging) stays as the plan says: unpacked, with the version check.

Tests: 162 (the detector's, the page-placement and theater-fit tests went with their code; new:
an old extension refused, the theater's report and mapping, the tray's theater request, old
settings loading).

First run in the glasses (1.4.0): the theater converted only once in six openings. In the
player's own full screen Edge reported the window as 1489×832 against a 1501×844 screen (CSS
px), so the size comparison said "not the screen". Fixed in 1.4.1: the player being the
document's full-screen element counts as on the screen; the page also reports what it measured
(`view`), and the log names which check failed (the page's, or no window covering the virtual
monitor) with the numbers. With it, 3D ran at 25 hand-overs/s (the video's rate). On WeTV the
player showed the subtitle language menu: `[class*="subtitle"]` matched the hidden menu, whose
`innerText` is all its text. 1.4.2: caption boxes must be visible, over the video, not
controls, menus or lists, at most 4 lines; WeTV's subtitles are `div.text-track > span`
(checked on the live site: of 14 matched boxes only the subtitle is kept).
Then the subtitle showed early, late or twice: the player put it 9 % above the screen's bottom,
which for a 2.39:1 video is the black bar under the picture, and the app delays only the
picture's rectangle (buffer 2 s), showing the rest live. 1.4.3: the caption is placed from the
picture's rectangle (inside its lower part, above the controls while they show), read on each
drawn frame instead of every 250 ms, and its box reported as soon as its text changes.
The buffer felt laggy: with the controls hidden only 18–19 of the video's 30 frames a second
went to the model and into the buffer (presents 38–41/s), so the delayed picture held frames.
The capture of a frame often reached the render thread before the page's report of it
(page → worker → socket), and was taken for the frame before, already handed over. Now
`PageVideo.TryFrameAt` counts frames on past the newest report at the video's cadence (at most
3, never while paused), and a frame is handed over only when newer than the last. The caption
also tore at a line change: its box came through the controller's 250 ms timer, after its
first frames had their depth, and the caption mask rose over 0.1 s. Now the link writes the
player's boxes straight into `PageVideo` with when they appeared (the page's `at`, app clock),
the depth thread takes for each frame the set on the screen at its capture (`PageBoxSource`),
and a box the page reports is flat from its first frame. The caption sits higher (10 % of the
picture above its bottom). Extension 1.4.4.

### Buffer and matched timing fixes (2026-10-12, user request: review of buffer and timing)

A read-through of the delayed picture found nine faults; all fixed, not yet run in the glasses.
- The held frames were shown only when a capture, the cursor or depth caused a redraw: up to a
  frame late, a cadence set by unrelated events, and a delayed tail that stopped when the page
  stopped repainting. `AiStereo.NextDueTicks` says when the next held frame is due; the session
  shortens its capture wait to it and redraws then.
- `DepthService` kept one untaken result, so look-ahead frames let go together (pause, seek,
  shorter look-ahead) were mostly lost and the second before each pause or seek got depth from
  up to a second away. With `KeepAll` (buffer, matched) results queue, oldest first; the
  look-ahead also goes down only when more than a frame too long (it wandered on 0.2 s at 30 fps).
- The buffer's memory cap counted 60 fps for every video and stamped frames after the swap
  chain wait: a 2 s 1080p buffer skipped frames of 30 fps video. It counts the page's frame
  rate, and frames carry their capture time (`CaptureUpdate.FrameQpc`), as do the model's frame
  times (the boxes' lookup and smoothing used the hand-over time).
- Matched timing learnt the look-ahead's hold as depth latency (300 ms and Auto on matched
  after a buffer); `DepthFrame.HeldMs` is taken off.
- Up to 600 MB of spare textures stayed on the GPU after the buffer; `Clear` frees them, the
  cap is an eighth of the GPU memory budget (128–600 MB). Nearness maps (1.27 MB at 756×420,
  a large object each) are reused instead of made per frame.
- Every depth result rebuilt the shown held frame though it was for one shown later;
  `VideoDelayBuffer.LastSources` says which frames' depth the shown one came from.
- The page's Web Audio output latency (50 ms in the run) is taken off the sound's delay.
- `Measures:` adds "held frames skipped" and "full GCs".
`EdgePipelineTests.TheEyesTakeUnderThreeMillisecondsOnTheGpu` failed (splat 6.2 ms), on the last
commit too: with the built-in screen as the main display, the tests' default adapter was the
Intel Iris Xe. The GPU tests now create their device on the high-performance GPU
(`TestDevice`), as the app's is; the test names it and measures 0.90 ms (splat) and 0.83 ms
(lookup) on the RTX 3060.

### The theater's controls live in the glasses (2026-10-13, user report: "the settings panel is glitchy")
The player's controls and 3D panel are drawn by the page over its video, so the glasses got
them with the picture: 2 s late with the buffer (a click, a hover, a new value all showed 2 s
on, under a live pointer) and bent by the depth. Now the renderer draws the boxes the page
reports as controls (`AiStereo.LiveControls`) from the live capture over the 3D picture, flat
in both eyes: those on the screen now and those in the held frame shown, so a closed panel
leaves no copy behind (for the delay, that place shows the live video instead). Extension
1.4.5 reports the boxes as soon as the controls show, fade or the panel opens or changes,
not on its 250 ms timer.

### Glasses run of the buffer and timing fixes (2026-10-13, 01:24–02:28, Release)

Extension 1.4.5 (protocol 3), Edge, one 1920×816 video at 25 fps in the theater, seven
openings over about an hour, 33 minutes in one stretch at the end. DA3 Small, then DA3 Base,
at 518×294; Matched, then Buffer 0.5 s from 01:30. Only the log is on record; what the user saw
was not noted.

- **Every frame reached the model and the buffer:** hand-overs 24–26/s for the 25 fps video
  (the run before: 18–19 of 30), held frames skipped 0, full GCs 0, holes 0.00 %, page clock
  within 0.7–1.0 ms. Each opening converted (full screen recognised 6–36 ms after the
  player opened).
- **Sound in step:** the page delayed the sound 0.45–0.46 s for the 0.5 s buffer (the Web Audio
  output latency, 42–50 ms, taken off as intended) for the whole stretch; "0.00 s late" only in
  the first second after an opening, before the element was taken in. "Plays the sound live
  again" only when the theater closed or left full screen.
- **Matched timing did not hop:** 300 ms at the first opening (depth needed 205 ms while the
  model warmed up), then 160 ms with 62–73 ms needed, held there.
- **Cost:** DA3 Small 6–10 ms + post 3.1–5.0 ms alongside; DA3 Base 10–12 ms + post 3.4–4.2 ms,
  model busy 25–29 %. GPU 0.8–2.1 ms a frame for the eyes. Presents 53–60/s, capture→vblank
  17 ms on average (max 40 ms). VRAM 620–655 MB, private memory steady at 1.6 GB over the last
  half hour (no growth).
- **Flicker on still pixels (M4) was higher than before:** 0.025 on average in the theater, up
  to 0.063, against 0.002–0.03 in the 10-11 run (other video, live and matched timing). Not
  explained yet; it may be the content.
- The glasses left the desktop twice (01:47, 01:50) and came back in 2D, then 3D; the session
  stopped and restarted each time and the open theater was picked up again. The window guard
  moved Edge and an ApplicationFrameHost window off the glasses meanwhile.
- One flat spell, at the first opening (depth 15 frames behind while the model warmed up).

Not tried in this run: 756×420 (the `post … ms` check), the buffer at 1 or 2 s, the live
controls with the buffer (to confirm by eye), Soft edges and Lookup A/B.

### The theater redone: the video alone, one opaque bar, captions found anywhere (2026-10-19, user report)

The user's report: the progress bar sometimes went transparent and showed the video playing
behind it; subtitles showed only when the site's own player was full screen; the theater should
show only the video, not the whole screen. Causes:

- **The bar:** it was a gradient over the picture with a see-through track, and the app drew
  from the live screen only the buttons', seek bar's and time's boxes. With the buffer, the live
  video showed through the seek bar while the picture around it was 0.5 s late; and where the
  controls had been in the held frame shown but no longer were, the app drew the live screen, a
  strip of video at another time for the delay's length.
- **Subtitles:** the player looked for caption boxes only inside `playerOf(el)`, the largest
  ancestor of the video at most 1.6× its picture. In the site's own full screen that is the
  whole player; in the page's layout the caption layer often sits outside that ancestor.
- **The whole screen:** the player had its own full-screen toggle (button, F, double click). Out
  of full screen the theater stayed open in the window, and the glasses showed the browser and
  the desktop flat ("waiting for the theater's video"), which the 10-13 log shows at 01:33 and
  01:48.

Extension 1.5.0 (protocol 3 unchanged), the theater section of `content.js` rewritten:
- The theater is full screen for as long as it is open: leaving the player's full screen (Esc)
  or, when the worker put the window in full screen, the window's (F11) closes it. No full
  screen button, no F key, no double click; a click on the picture plays or pauses at once.
- One bar at the bottom, opaque (seek bar, time, ±10 s, play/pause, mute, 3D, Exit), shown and
  hidden at once (no fade), reported as one box; the 3D panel above it, opaque, one box.
- `AiStereo.LiveControls` draws only the boxes on the screen now. Where the bar was in the held
  frame shown, that frame shows it as captured: it lingers for the delay after hiding, flat,
  instead of a hole onto the live video. A closed panel lingers the same way.
- Captions: caption-like boxes anywhere in the document (and in the video's shadow root) that
  do not hold the video, are not controls or menus, are visible and lie mostly over the video;
  found again once a second, read on every frame drawn.
- A viewport the screen's size now counts as the screen (window full screen), as well as the
  window's outer size.

Tests: 168, Debug (the Release copy was running).

**First try (09:23–09:31, user):** the solid black bar looked bad; the 3D panel was slow to
close (it lingered in the delayed picture for the buffer, as the change above made it); after
Exit the site stayed full screen; subtitles still came only when the theater was opened from
the site's own full screen. The log agrees: openings from the site's full screen found a caption
(09:25:27), openings from the page found none. Extension 1.5.1:
- **Subtitles:** checked on WeTV (Playwright, the live site): in the page's layout its caption
  (`div.text-track`, two levels above the video, inside `.player__wrapper` six up) is visible
  over the video, but once another element is full screen WeTV lays the `<video>` out at no
  height (937×0, then 1560×0) while the caption stays shown, so the over-the-video test failed.
  A video box with no size is no longer a test; the caption boxes are looked for within six
  ancestors of the video (below the body) instead of the whole document, so the page's own
  "subtitle" texts elsewhere cannot come in. Checked the same way: with an element full screen
  and the video collapsed, each subtitle line was read as it changed.
- **The bar** floats (16 px in, rounded) and is frosted: tinted 50 % and blurred 28 px, as is
  the panel. The app still draws it from the live screen; through the blur the live video
  under it is only colour.
- **The panel closes at once:** it is reported as kind "panel" (`PageBox.IsPanel`,
  `BoxSet.Panels`), and `LiveControls` draws its place in the held frame shown from the live
  screen again. The bar still lingers for the delay when it hides.
- **Exit:** closing leaves every level of full screen (the site's too), so the page is back in
  its window.

Tests: 168 (the overlays test now carries a panel box).

**Captions read only when they change** (extension 1.5.2, user request: "optimize its
performance"). Each read makes the page under the player lay itself out (visibility, boxes,
`innerText`), and the theater read on every frame it drew plus four times a second. Measured on
WeTV (Playwright, another element full screen as in the theater, one caption box): 0.17 ms a
read (worst 0.6), and read on every animation frame 2032 reads in 15 s, 23 ms of the page's
main thread a second, for one caption change. Now a MutationObserver on the page's player
(child lists, text, class/style/hidden) marks the captions changed when a record touches a
caption box or something holding one, a new caption-like element marks the boxes to be found
again, text tracks report `cuechange` (and `addtrack`, `change`), and the animation loop or the
next drawn frame reads once. Once a second they are found and read anyway (a class given
later, a style above the player). Checked on WeTV by seeking (an ad held the video paused):
79 page changes, 2 reads, every text the page showed seen.

**YouTube stuck in full screen after the theater** (user report, 1.5.2: captions "working
great"). Opened from YouTube's full screen, the player went full screen over it, and closing
left both, one after the other; YouTube kept its full-screen layout and its button said "exit
full screen" with the document no longer full screen. 1.5.3: when the page is already full
screen the player goes inside its full-screen element and uses that full screen; closing ends
it once, as YouTube's own button would. Leaving that full screen (Esc) closes the theater.

### UI sweep: one place for each control (2026-10-19, user request: "remove redundant features, text and buttons")

Each control now lives in one place, the one where it is used:
- **Tray** (12 entries → 7): status, Pause, Whole screen in 3D (a check, Ctrl+Alt+D), 3D
  theater, Show in a window, Settings, Tuning, Exit. Gone: Video stretch and Video timing (the
  theater's panel and Settings), Capture (see below), the AI 3D submenu and its model line (the
  tooltip and Settings say it), Open log.
- **Settings** (six cards → five, about 20 controls → 9): status with the model line; Displays
  (the two pickers, "also show in 2D"); 3D (whole screen, model, resolution, timing); the
  browser extension (status and how to add it); startup and shortcuts. Gone: the session's
  measurements (they are in the log), the capture method, the cursor and window guards, "show
  in a window when not on the glasses" and "move windows off on exit" (always on now), depth and
  screen plane (Tuning, the panel, the hotkeys), what the video is and the stretch (the panel),
  the theater-button checkbox (the button shows only while the glasses are in 3D anyway), the
  sound offset (Tuning), the model folder line, and most of the long captions.
- **Tuning**: no stretch slider (the panel), no status and measurements card.
- **The theater's 3D panel** (extension 1.6.0): no edge-snapping and flat-subtitle switches
  (Tuning has them). The status message no longer carries `offer`.
- Settings removed from `AppSettings`: `KeepCursorOffGlasses`, `MoveWindowsOffGlasses`,
  `ShowInWindowWhenNotOnGlasses`, `MoveWindowsOffVirtualMonitorOnExit`, `OfferTheater`; old
  files with them load (unknown properties are ignored). `CaptureMethod` stays in the file for
  troubleshooting (Windows.Graphics.Capture), with no control.

Tests: 168.

### Video Depth Anything Small, streaming: the spike (2026-10-19, user request)

improvement-plan.md §10.2, with its go/no-go: at most 25 ms a frame at 518, and M4 at least 30 %
lower than Distill-Any-Depth Base.

- **Export.** The authors' code (DepthAnything/Video-Depth-Anything at 4f5ae23) and the Small
  checkpoint (Apache-2.0, 28.4 M parameters, `video_depth_anything_vits.pth`, SHA-256
  13379300…3609). Their streaming mode runs the encoder on the new frame alone; the head's 4
  temporal modules × 2 attention blocks also read the hidden states the frames before left.
  Exported as two static graphs a size: the first frame (image → depth, state) and the step
  (image and 31 past states → depth, state); a state is the 8 tensors flattened, 956,160 floats
  at 518×294. The window is theirs: 32 copies of the first frame's state, then that frame kept
  for good and the 41 newest; each call reads the first two and the last 29. Against their
  `infer_video_depth_one` on 60 frames (CPU, fp32): at most 3·10⁻⁵ of the depth range.
- **TensorRT for RTX gave NaN.** The encoder was right; 3 of the 4 temporal modules were not:
  its fused attention (MatMul–softmax–MatMul, also with the softmax written out) fails for heads
  of 8 and 24 channels; the 48-channel module was right. The export writes that attention
  without matrix products (one query over at most 32 keys: products and sums; the same maths,
  3·10⁻⁵), and without baddbmm's `torch.empty`, which tracing had turned into a constant of
  leftover memory. Then fp16 (constants folded first) on TensorRT against fp32: 0.04 % of the
  range on average, r 1.0000, through the window's slide at frame 11.
- **Speed.** Python, states on the GPU: a step 8.0 ms (p95 8.8), a first frame 4.5 ms. In .NET,
  with the image and depth over the bus: 8.9 ms against Distill-Any-Depth Base's 8.4.
- **In the app** (a trial, at Balanced 518×294 and Quality 756×420; not Fast): `DepthTier.VdaSmall`,
  `VideoDepthEstimator` (two sessions; a ring of 43 state tensors on the GPU, 164 MB, bound in
  the window's order through I/O binding, no copies; `Reset`), the depth service resets it at
  seeks, region changes and scene cuts (luma before the model runs), engine caching shared
  (`DepthEstimator.Open`). Files: `vda-small_{H}x{W}.onnx` and `vda-small-first_{H}x{W}.onnx`
  (55 MB each) in the models folder. In the Settings model list as a trial. At 756×420 a state
  is 1,969,920 floats (the ring 339 MB); a step 16.0 ms (p95 17.2), a first frame 21.9 ms;
  fp16 on TensorRT within 0.03 % of fp32 over 60 frames.
- **The gate** (`TemporalDepthTests`, 150 consecutive frames of the sample clip, all distinct):

  | | ms/frame | M4, the model alone | M4, through the depth service |
  |---|---|---|---|
  | Distill-Any-Depth Base 518×294 | 8.4 | 0.0057 | 0.0013 |
  | Video Depth Anything Small 518×294 | 8.9 | 0.0021 | 0.0011 |
  | Distill-Any-Depth Base 756×420 | 17.9 | 0.0042 | 0.0010 |
  | Video Depth Anything Small 756×420 | 17.0 | 0.0020 | 0.0010 |

  The model alone flickers 64 % less at 518×294 and 53 % less at 756×420; through the depth
  service, whose temporal smoothing already takes most of Distill-Any-Depth's flicker away, 14 %
  less and the same. At Quality it is slightly faster than Distill-Any-Depth Base. **No-go by the plan's
  measure** (30 %). Near and far agree (ρ 0.98–0.99 with DA2 Small and DAD Base). What M4 does
  not see (moving parts, depth breathing over seconds, the smoothing's lag) only the glasses can
  show; the trial model lets that be judged before it is removed or kept.
- **Found on the way:** `SampleClip` asked `GetThumbnailsAsync` for times 1/30 s apart and got
  one picture for all, so `ParallelSpeedTests` had timed one picture 48 times. One thumbnail a
  call now; on 48 consecutive frames post-processing takes 2.2 ms at 518×294 and 3.1 ms at
  756×420 (parallel; 4.7 and 8.7 on one core), not 3.3 and 5.7.
- The export scripts (export, validate, fp16, bench, the TensorRT checks) and both venvs are in
  this session's scratchpad (`vda\`), not the repo: Python 3.10 with CPU PyTorch for the
  export, Python 3.14 with ONNX Runtime 1.31 for TensorRT (the provider needs API 24+).

Tests: 169, Release.

### The buffer without gaps (2026-10-19, user request: "even 2 seconds buffer doesn't matter as long as there is no interruptions")

The buffer interrupted playback in three places: opening the theater (picture held, sound
silent for the buffer's length), pause then play (after a pause both ran on for the buffer's
length, and after play came that long of frozen picture and silence), and seeks (the old
position went on for the buffer's length). The user chose a spinner for the waits that cannot
be avoided. Extension 1.7.0:
- **Pause holds both delay lines.** The page suspends its AudioContext on the theater video's
  `pause` (not on a network stall: the buffered seconds play on and the stall comes that much
  later, as it happened) and resumes it on `playing`; the delay line keeps the seconds not yet
  heard. The state message carries `held` and `at` (app clock); `PageVideo.PlayTicks` is a clock
  that stops while held, and `AiStereo` stamps and chooses held frames on it, so the picture
  stands with the sound and goes on from the same frame. A context we suspend is not reported as
  blocked (the first page test caught that: the app would have dropped the buffer on every pause).
- **A seek starts the page's line again** (silent for the delay) and reports `seekAt`; the app
  drops the old position's frames not yet shown (`VideoDelayBuffer.DropBetween`), so the frame
  on the screen holds until the new position's come due. A looping video's jump back to the
  start plays on through.
- **A spinner** (frosted, centred, a live box like the bar) shows while a new line of 0.5 s or
  more is silent: opening the theater, after a seek, not while paused. The time and the seek
  bar show the time seen and heard (the video's less the delay), and ±10 s steps from it.
- **A live stream** (`live`: no end) is never buffered. New settings default to a 2 s buffer
  (`BufferSeconds`).
- Checked with the real `content.js` in a test browser on a local page (the worker stubbed, its
  messages recorded): the spinner for 2.0 s after the delay starts and after each seek, none at
  a loop's jump, `held` on pause and off on play, the media still delayable while held.

Tests: 172 (the play clock, the seek drop, the new state fields).

**First run (14:55–15:01, user: "the extension crashes mid way after some pause and seek").** Not
a crash: at play after a pause (15:00:02) the page reported its sound "blocked" for the moment
its suspended context took to resume, and the app remembers a refusal for good, so the buffer
was off for the rest of the theater (sound live, the panel's buffer steps greyed). 1.7.1: a
context that has run once is never "blocked" (`contextRan`), and the app no longer remembers
"blocked" (only protected and cross-site media). The video was 3840×2160 at 60 fps.

### Depth ahead from the player's own buffer: the check (2026-10-19, user request: "can we tap directly into that?")

The idea: a page cannot read decoded frames ahead of the one shown, but the site's player hands
its `<video>` compressed segments seconds ahead through Media Source Extensions. A hook on
`SourceBuffer.appendBuffer` in the page's own world, demuxing and decoding them with WebCodecs,
would let the app have each frame's depth before it shows: no picture or sound delay at all.
Checked in a test browser (Chromium 155, the spike's hook and a minimal fragmented-MP4 demuxer in
the scratchpad's `mse\`, not the repo):

| | YouTube (Big Buck Bunny 4K60) | WeTV (a free episode) |
|---|---|---|
| Fed through | MSE, the main page, no worker | MSE, the main page (two workers, not the media's) |
| Format | AV1 in fragmented MP4, Opus in WebM | H.264 in fragmented MP4, AAC |
| DRM | none | none on this episode (the site asks for Widevine and PlayReady) |
| Ahead of playback | 16–34 s; 20 s before play | 190 s |
| Decoded | every sample, across a 360p → 720p switch (2,280 frames, 48 s) | every sample (4,745 frames, 190 s) |
| Decode, scale to 756×420 and read back | 149 fps at 3840×2160, 256 at 1080p, while the page plays the same 4K60 (5 of 5,150 dropped) | 308 fps at 864×486 |
| Decoded timestamps against `requestVideoFrameCallback`'s `mediaTime` | equal (1 µs rounding) | equal |

So it is feasible on both. What the build would need: a MAIN-world content script at
`document_start` (all frames) wrapping `addSourceBuffer`/`appendBuffer`/`remove`; demuxers for
fragmented MP4 and WebM (YouTube can serve VP9 in WebM); decoding, scaling and readback in a
worker, not on the page's thread (6.4 ms a 4K frame here); sending model-size frames keyed by
media time (756×420 RGB at 60 fps ≈ 57 MB/s, or luma plus a quarter-size chroma); the app
running depth ahead into a store by media time and taking each live frame's depth by the
page's frame reports. A second MediaSource after a quality change, seeks (segments for the new
place) and `timestampOffset` are part of it. Protected media, players in workers and plain-file
videos (no MSE: a hidden second element would do) stay on today's delay.

### Two layers: the player delays its own picture (2026-10-19, user report: "whenever a setting panel or a loading spinner shows up there is a transparent cutout ... after these are closed there is a big cutout on the video")

**Why it kept breaking.** The app captures the screen, where the browser has already merged the
video and the player's controls into one picture, and with the buffer showed that capture 2 s
late. So the controls in the delayed picture were 2 s old; patched in from the live screen
(`LiveControls`) they showed the video 2 s ahead, flat, through the frosted panel and spinner
(the see-through cutout), and after a panel closed the delayed picture still had it for 2 s,
patched live again (the big cutout). The video under an old panel is in no capture: no patch
in the app could be right.

**Now the two layers are made in the page** (extension 1.8.0). While the sound is delayed, the
player keeps each frame the video presents in GPU memory (WebGL 2; luma and half-size chroma,
1.5 bytes a pixel at the size shown, 640 MB at most: 2 s of 1080p at 60 fps is 373 MB) stamped
on the audio clock, and draws it when its sound is heard, with the caption read with it; its
bar, panel and spinner are live on top. The screen is always the finished picture and the app
shows it as it is, never late. For depth ahead, the player sends each frame as it plays at the
model's input size (JPEG, about 55 KB, `picture`); `PagePictures` decodes it into the model
input, hands it over in order and files the depth by line (the player's count of jumps) and
time in the video; the render thread takes the depth of the frame the player reports on the
screen (its own, or interpolated between neighbours, or the newest before it in live timing).
The page's caption boxes are held flat at render time (`CaptionLock.ApplyGiven`), the controls
drawn flat from the capture. A pause suspends the audio clock (sound and picture hold); at the
end the tail plays out; a seek drops the kept frames not shown, starts the delay line again and
a new line, and the spinner stays until the new place's first frame; a paused seek shows its
frame at once. A video the player may not read (another site's without CORS) and protected
media play live with depth from the screen.

Removed: `VideoDelayBuffer` (and its tests), the app's held-frame picking, the play clock
(`PlayTicks`, `held`/`at`/`seekAt`), `NextDueTicks` and the held-frame memory budget, the
panel-in-held-frame patch, `NearnessBlend.Weighted`, the 90 ms live-sound matched cap.

Checked with the real `content.js` in a test browser (worker stubbed, clock answered; the clip
served over HTTP and, for seeks, as a blob): frames drawn 1.9–2.0 s behind the video
throughout; the spinner the first 2 s; pause at 5.01 s stopped the picture at 3.867 and play
went on with 3.900; a seek to 7 held the last frame under the spinner and drew 7.033 on line 8
2 s later; a loop's return played through; a paused seek showed its frame at once and played 2 s
after play. Screenshots: the delayed picture upright, true colours, sharp at 2151×1210.
Tests: 165 (4 new for `PagePictures`), Release.

First look in the glasses (user: "working great"), one fault: the bar and the panel had a
shadow, a doubled background. The renderer draws them flat over their boxes, but the depth
under them was the video's, so the warp moved their pixels in each eye and that copy stood out
around the flat one (the old path had held their boxes flat in the depth thread; the new one
kept only the captions). Now the depth under them and 3 model px around is the screen plane
at render time (`PageBoxes.Flatten`). Extension unchanged.

And "sometimes the play/pause button is not responsive": the player rewrote the play and mute
icons on every mouse move, so a press on the icon with any movement before the release lost its
click (the pressed element was gone); the 3D panel was rebuilt on every new value, losing quick
second presses the same way. Extension 1.8.3: icons replaced only when they change and never
the click's target, the panel built once. In a test browser, press, move 2 px, release on the
play icon: 0 of 6 toggled before, 6 of 6 after; three quick presses on Depth + all reached the app.

### Packaging prepared, documents polished (2026-10-19, user request: "prepare for packaging and polish all the documents")

- **A package**: `packaging/publish.ps1` publishes a self-contained, ReadyToRun win-x64 folder
  and zip into `artifacts\ImmersiveSBS-<version>-win-x64` (its own build folder, so a running
  copy of the app cannot lock it), with the extension, LICENSE, README, THIRD-PARTY-NOTICES and
  ONNX Runtime's licence files, and with `-Models` the shippable model families (it refuses the
  non-commercial ones). Measured: the app 247 MB, Distill-Any-Depth Base at three sizes 564 MB,
  the zip 613 MB. The packaged copy started on this PC, connected to the extension, opened the
  glasses session and loaded a model.
- **Models beside the app**: `DepthModel` looks in the app's `models` folder first, then in
  `%LOCALAPPDATA%\ImmersiveSBS\models`; the "Depth: loaded" log line names the file
  (`DepthModelTests`).
- **A version**: `<Version>1.0.0</Version>` in the project, in the log's first line and the
  Settings window's title (`AppController.Version`).
- **THIRD-PARTY-NOTICES.md**: every shipped library with its licence and copyright (all MIT),
  the models' licences and sources, and what is used but not shipped (TensorRT for RTX, the
  driver).
- **Documents**: README rewritten for users (requirements, getting started, timing, shortcuts,
  privacy, troubleshooting); `docs/packaging.md` (how to build a release, what the user's PC
  needs, where the app keeps things, the decisions left, a release checklist); status lines
  brought up to date in `docs/plan.md` (per phase), `improvement-plan.md`, `video-3d-plan.md`,
  `extension-first-plan.md`; the extension's README and CLAUDE.md caught up with the theater.

Tests: 167, Release.

### A one-click installer with the driver (2026-10-20, user request: "the virtual driver bundled inside an installer ... a one click installer, not having to go to places")

`packaging/build-installer.ps1` builds `artifacts\ImmersiveSBS-Setup-1.0.0.exe` (575 MB, about 3
minutes) with Inno Setup 6.7.3 (a portable copy in `artifacts	ools`, its download signed by the
maintainers' company). No pages before it installs: Windows' administrator prompt, a progress
bar, a finish page. It installs the app and Distill-Any-Depth Base in Program Files, and the
Virtual Display Driver 25.7.23 (downloaded once, checked against its SHA-256, every file's
signature checked) unless a `Root\MttVDD` device exists: SignPath Foundation into Trusted
Publishers first (no prompt), then `devcon install`; the driver's settings set to one
1920×1080 monitor at 60 Hz if it has none (the driver reads `C:\VirtualDisplayDriverdd_settings.xml`).
The finish page, all ticked: start with Windows, open Edge's extensions page with the extension
folder, start the app. Uninstalling asks before removing the driver it added. Details and what
was checked in `docs/packaging.md`: a throwaway setup running only the driver check found the
driver on this PC (so a real install here skips it); `certutil -verifystore` answers 0 whether a
certificate is there or not, so the store's registry key is read instead. Not run end to end
yet: that needs a PC without the driver.

### TensorRT for RTX on a fresh PC: the app asks Windows ML (2026-10-23, user request: "do 3", after the move to a new PC)

The new desktop (Windows 11 26H2 build 26300, RTX 5060 with driver 32.0.16.2101, Intel UHD 770)
had no `TRT-RTX` package. Windows ML's catalog lists `NvTensorRTRTXExecutionProvider` as
NotPresent; `EnsureReadyAsync` fails after ~5 s with 0x80073D3B ("not applicable"), with Windows
ML 2.1.74, 2.2.12, 2.3.42 and 2.4.89 alike. Windows Update's trace: it finds
`Windows.Workload.ExecutionProvider.TRT.amd64` (metadata 2.2609.1.0), then
`GetExtendedUpdateInfo` returns 8024000C after "still missing fragments", and the search ends in
0x80070057. No pause, restart or policy blocked it, and other scans succeeded, so it looked like
incomplete metadata on Microsoft's side; it was the licence (below).

`TensorRtRtx` now asks Windows ML itself when no package is found (once per run; the status
line says it is installing; the log has the result and the error). Only Windows ML's catalog is
taken from `Microsoft.Windows.AI.MachineLearning` 2.4.89: the package carries ONNX Runtime 1.27
under the same file names as the app's 1.30, so its assets are excluded and the projection and
`Microsoft.Windows.AI.MachineLearning.dll` are referenced by hand; that DLL links
`onnxruntime.dll` and runs on the app's 1.30. Checked on this PC: the catalog loads in the app's
process, the request is made and its failure logged.

**The cause was the Windows licence key** (found by the user): Windows Update delivers the
workload only to a retail-channel licence. With the key changed to a retail one it installed
on 2026-10-24 at 08:32 through that path: "Windows ML installed
TensorRT for RTX in 16 s", 2.30.49.0, as `Microsoft.WinML.NVIDIA.TRT-RTX.EP.Framework.2` (a
framework package; the old laptop's was `MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP.2`, so
the package search now matches `WinML.NVIDIA.TRT-RTX.EP` anywhere in the name). On the RTX 5060
(`DepthEstimatorTests`, Release; engines compiled in 4–6 s each): Distill-Any-Depth Base
6.2 ms at 518×294 and 10.8 ms at 756×420; Depth Anything 3 Small 2.7 / 6.0 ms; Depth Anything
3 Base 5.7 / 11.9 ms. All 169 tests pass with the models run.
Its licence is Microsoft's own, not MIT (`docs/packaging.md` decision 7).

The models moved with `ImmersiveSBS-models\` (not in git); `%LOCALAPPDATA%\ImmersiveSBS\models` is
a junction to its `models` folder. Depth Anything V2 (Small, Base, Large) was deleted at the
user's request.

The Fast resolution (392×224) is removed (user: "remove low resolution"): Balanced and Quality
are left, a saved "Fast" loads as Balanced (`DepthPresetConverter`), and `publish.ps1` no longer
packages the `_224x392` files.

### The installer on the new desktop; models and timing choices (2026-10-25, user: "the install and uninstall is perfect")

The installer was rebuilt here (Inno Setup 6.7.3 portable again, from its GitHub release, signed
by Pyrsys B.V.) and the user ran it and its uninstall on this PC: the driver installed, the app
ran, and the uninstall removed both. Then:

- **Models: Distill-Any-Depth, Depth Anything 3, Video Depth Anything; no Depth Anything V2**
  (user: "I want DA3 and DAD and VDA, no da v2"). The Small tier is gone (a saved "Small" loads
  as Distill-Any-Depth Base; the model-agreement test compares with Distill-Any-Depth Base now),
  and the installer ships all four families at both sizes by default: 1,184 MB of models, an
  installer of 1,030 MB (405 MB with Distill-Any-Depth Base only).
- **Every timing choice can be chosen.** The buffer choices were greyed in Settings while no
  browser was connected, so Settings opened before the browser could not pick them (user: "cant
  choose buffer time because of the extension"). The check gained nothing: timing applies only
  to the theater, which is the extension's, and a video the page cannot delay plays live anyway
  (`BrowserCanDelay`). Removed, with the theater panel's stepping (`NextTiming` now just moves
  one choice). Then the Timing box left Settings altogether (user: "remove the timing setting in
  the app keep the theater pannel only"): Settings says it is in the panel, and Save keeps what
  the panel chose.
- **Video Depth Anything checked** (user: "check the logic for video depth anything again").
  The window was compared step by step with the authors' `infer_video_depth_one`
  (`video_depth_stream.py`): 32 copies of the first frame's state, each step reading entries
  `[0:2] + [-29:]`, the entry after the first deleted from the 11th frame (`id + 32 > 42`); it
  matches, and is now `StateWindow`, tested against those list operations over 150 frames, a
  reset and 60 more (`StateWindowTests`). One fault fixed: a reset was a flag read when the
  depth thread started its next frame, so a frame submitted before a seek but run after it
  became the first frame of the new window, which the model keeps for good. Each frame now
  carries the count of resets at its submission, and the model starts over at the first frame
  with a new count (`AModelWithMemoryStartsOverAtTheFirstFrameSubmittedAfterAReset`). On a
  drawn scene its output orders near and far like Distill-Any-Depth Base (ρ 0.92), inverse
  depth as assumed. Not measured here: its flicker against the default (`TemporalDepthTests`
  needs the sample clip, still on the old laptop).
- **Code cleanup** (user: "verify and clean up the codes in the entire codebase"). The analyzers
  (unused members, parameters, values, usings) and a scan for declared members named nowhere
  else in the code, tests, XAML and extension found: Win32 declarations nothing calls
  (`RegisterHotKey`/`UnregisterHotKey`, `EnumDisplayDevicesW` and its struct, `CloseHandle`,
  six constants), and five members left by earlier removals (`AiStereo.PresentLatencyTicks`,
  `DepthPostProcessor.CaptionBox`, `MatchedDelay.PeriodMs`, the readback's `Discard`,
  `DisplayInfo.FrameShape`); all removed. `ButtonImages` is `BadgeImages` (it draws only the
  model badges since the app's own buttons went). Kept on purpose: Win32 struct fields (their
  layout), the constant buffer's `Pad`, the XAML attached property's setter, the members the
  tests read (`GpuTimer.TakeAverages`, `PageVideo.Latest`), the extension's `pong` (it keeps the
  service worker alive; the app ignores it). The extension has no unused function or variable,
  every message type it sends the app handles, and the packaging scripts parse.
- **Simplified** (user: "simplify my codebase"; four reviews: reuse, simplification,
  efficiency, altitude). Same behaviour, 169 tests passing:
  - The theater's timing is a typed `TheaterTiming` (mode and delay), not a text the panel
    parsed back; `wantedDelaySeconds` (a copy of `DelaySeconds`) is gone; the model input's
    hand-over, the flat split and the synthesis are one helper each in `AiStereo`.
  - A second path for the player's boxes, kept only for log counts, is gone (`VideoHint` box
    lists, `BrowserConnection.Overlays`): the counts come from `PageVideo.BoxesAt`.
  - One copy each of: the eye fit (`TheaterGeometry.Fit`, for the renderer, the badge and the
    session), the ImageNet constants (`ImageNet`), the capture texture (`CaptureTexture`), the
    lenient enum converters (`NamedEnumConverter<T>`), the "on the glasses" test, the status
    line, the ping-pong GPU pass (`NearPass`), the GPU test helpers (`TestDevice.CreateBgra`,
    `ReadEyes`) and the depth statistics (`DepthStats`). `PageVideo.TryFrameAt` owns its slack.
  - Cheaper: the memory figure reads this process only (`GetProcessMemoryInfo`, not a snapshot
    of every process each second on the render thread); the browser's process name is looked up
    only for the log line that needs it; the stray-window guard asks a window's class and
    process only when it is on the glasses; each picture is base64-encoded by the browser
    (`FileReader`, checked identical in a browser) instead of through JS strings; the theater's
    time text is written only when it changes; the browser link reads a message's type once.
  - The packaging scripts read the package versions from the project file, and
    `publish.ps1` returns its folder and version to `build-installer.ps1`.
  - Left for a run in the glasses: re-using the theater's eye images when nothing changed
    (`AiStereo.FromPage`), a dirty rectangle for `CaptionLock`'s mask, filling the model's input
    buffer without a second copy under the depth service's lock, an asynchronous log writer,
    one base class for the two input hooks.

### Model loading, from the user's run (2026-10-25, user: "something is off with model loading and unloading ... make sure model is load as soon as the app start")

The run (14:44–14:59, all four models in the theater, Release, the new extension) worked, and
showed three things. The chosen model did load at start, AI 3D on or not (`UpdateDepthService`);
the waits were elsewhere. Measured with a throwaway console on Distill-Any-Depth Base 756×420
(engines folder backed up and restored after):

- **A model's first use on a PC builds its engine** (load 8.9 s against 1.1–2.3 s cached; 26 s
  for Video Depth Anything's two graphs), so the first change to each model waited and the later
  ones did not. Now `EnginePreparer` builds every offered model's engines in the background
  after start, one at a time at low priority, only while AI 3D is off and the chosen model is not
  loading. Checked: with one engine missing it waited while not allowed, then built only that
  one (4.2 s, kernels 0.6 s) and stopped. The background build and a model load take the same
  engine's lock, so they never build one twice.
- **The session made right after a build kept 1.5 GB more** (2,335 MB against 840 cached; the
  run's 6.9 GB with Video Depth Anything), until the model was unloaded. After a build one
  session is now made and dropped, and the kept one loads from the runtime cache: 1,130 MB, for
  1.3 s more once.
- **The first frames were slow**: the first inference 45–480 ms (10 after) and the
  post-processing 48, 10, 7 ms (6 after). The depth service now warms both, on a test picture
  and a post-processor of its own, before it says Ready: the first frame through it took 10 ms
  in the model and 19 ms from submit to depth (`WarmsTheModelBeforeItIsReady`).
- **The app redrew 32–50 times a second with nothing changing** after the theater closed: depth
  still queued from it kept the session's "new depth" check true while AI 3D was off, and nothing
  took it. With AI 3D off `AiStereo` now takes it and stops the service keeping results.

The reset test's `Started` signal was set by the warm-up's runs, which made it fail now and then
under load; it is cleared with the warm-up's log. 170 tests pass (three full runs).

### Pause on a model change, VRAM, and the temporal model measured (2026-10-25, user: "the player should pause when changing model ... check if the vram is optimize ... should we keep only temporal? Is our temporal logic optimize yet? Do deep research")

- **The theater pauses while a model loads.** The status tells the page `loading` (a model or
  size chosen, or the first load; glasses in 3D, the video converted), sent at once from
  `Evaluate`, not at the next 250 ms tick. The player pauses a playing video with its spinner
  and plays on when the model is ready; a video already paused, ended or live is left alone, and
  one the user plays meanwhile is theirs (`holdForModel`). The model takes no frames until it is
  ready, so those played meanwhile had no depth.
- **A model swapped out while it loaded blocked the UI and could stay in GPU memory.**
  `DepthService.Dispose` waited up to 3 s for the worker on the UI thread (the run's clicks
  through models at 18:41:20–23 held it 2.3 s), and past 3 s (a first-use build) left the model
  loaded until the process ended. The worker now unloads its own model when it ends; a loading
  one is not waited for (`AModelSwappedOutWhileItLoadsIsUnloadedOnceLoadedWithoutWaitingForIt`).
- **VRAM** (the run, this process on the render adapter, of 7.1 GB): 1.0 GB with Video Depth
  Anything Small 756×420 loaded and 3D off, 1.04 in the theater; 1.10–1.12 with
  Distill-Any-Depth Base and 1.16 with Depth Anything 3 Base; one model is loaded at a time.
  The largest single part is Video Depth Anything's state ring: 43 × 7.9 MB = 339 MB in fp32
  at 756×420. Stored in fp16 (the model runs in fp16, so nothing is lost) it would be 170 MB;
  its two graphs each hold the same weights (55 MB). Both need a new export; not done.
  Process memory (2.6–2.8 GB in the theater) is mostly the runtime's; the managed part's swings
  (160–740 MB) are garbage collected late, not kept: a picture is one byte array, read into a
  reused buffer.
- **Every model on 150 consecutive frames of the sample clip** (`TemporalDepthTests`, now all
  four; the clip is in `%LOCALAPPDATA%\ImmersiveSBS\samples` on this PC):

  | 756×420 | ms/frame | M4 alone | M4 through the service |
  |---|---|---|---|
  | Distill-Any-Depth Base | 10.4 | 0.0042 | 0.0010 |
  | Depth Anything 3 Small | 4.8 | 0.0096 | 0.0025 |
  | Depth Anything 3 Base | 11.1 | 0.0105 | 0.0027 |
  | Video Depth Anything Small | 11.3 | 0.0019 | 0.0010 |

  At 518×294: 4.9, 2.5, 5.4 and 5.8 ms; M4 alone 0.0057, 0.0110, 0.0117, 0.0020; through the
  service 0.0013, 0.0033, 0.0030, 0.0011. Depth Anything 3 flickers 2.3–2.7 times as much as
  Distill-Any-Depth Base, alone and after smoothing. Video Depth Anything alone flickers 36–45 %
  as much, but the service's per-pixel smoothing (`Smoothing` 0.2) evens the two out on still
  pixels, at the cost of lag where things move.
- **Smoothing against lag** (a scratch console on the same frames; trail: mean |output −
  output unsmoothed| on pixels whose luma moved more than 6/255). Distill-Any-Depth Base: 0.2 →
  M4 0.0010, trail 0.0021; 0.35 → 0.0016, 0.0017; 0.5 → 0.0022, 0.0013. Video Depth Anything:
  0.2 → 0.0010, 0.0018; 0.35 → 0.0013, 0.0012; 0.5 → 0.0015, 0.0008. At every level the
  temporal model has less flicker for the same lag; at 0.35–0.5 it has the default's flicker or
  near it with half the lag. The percentiles' time constant (0.5 against 0.2 s) changed nothing
  on this clip. A two-way per-pixel filter (forward and backward over frames held for the
  buffer) gave 10 % less flicker for every model and a third less trail for Video Depth
  Anything only: not worth a backward pass over ~45 held frames each frame.
- **What the streaming mode costs** (the authors' README): it is training-free, and against
  their offline mode (windows of 32 frames, 10 kept from the window before, aligned by scale
  and shift, 8 blended) it loses accuracy: ScanNet δ1 0.926 → 0.836. The offline mode needs 22
  frames ahead (0.73 s at 30 fps) plus a window's run: it fits the theater's buffer (1–2 s), not
  live or matched timing. Only Small is Apache-2.0 (Base and Large are CC-BY-NC-4.0).

### Smooth motion: the theater at 60 fps (2026-10-25, user: "i want to view the playback at 60 fps")

Every video in the runs played at 25 or 30 fps; the glasses show 60 Hz, so each frame was shown
two or three times. Now the player draws the frames between (extension 1.9.0):

- **Where:** in the player, while it shows the picture late with the sound (the buffer, matched
  timing). It keeps each frame until its sound is heard, so the next one is already there: no
  delay is added. Live timing has no next frame and shows the video's own. Only below 40 fps.
- **How** (`extension/motion.js`, loaded before `content.js`): luma pyramids at 1/4, 1/8, 1/16
  and 1/32 of the size shown; at each level, block matching of 5×5 texels for the motion at the
  moment half-way between the two frames (so every pixel of a frame between has one: no holes),
  from the coarser level's motion here and at its four neighbours, ±1 px (±4 at the coarsest),
  a cost for straying from them and a small bonus for none; a 3×3 vector median; then a quarter-
  px refinement of each pixel's own motion and another median. A frame between is both frames
  moved along it and mixed by the phase; where they still differ by over a quarter of the range
  (a cut) the nearer one alone. The phase is where the moment due falls between the two frames'
  audio-clock times, in 16ths; each step is drawn once.
- **Tuned and measured** in a browser page (Chromium through Playwright; a scratch page, not in
  the repo) on the sample clip: the frame made between frames k and k+2 against the real
  k+1, PSNR over 25 pairs. Repeating frame k (what was shown): 32.8 dB; mixing the two: 37.1;
  motion: 38.3. Four frames apart (more motion): 28.8, 32.3 and 35.9. Better than repeating on
  every pair. The first try was below mixing (35.4): the coarsest pass read the texture it
  wrote (WebGL refused that draw), the motion was in whole px of the 1/4 level (4 px shown), and
  the fallback to the nearer frame at small differences cost 1 dB. A new pair takes 1.55 ms of
  GPU at 1280×720 (about 3.5 ms at 1920×1080: a tenth of the GPU at 30 pairs a second); a
  frame from a pair found, 0.07 ms. Needs `EXT_color_buffer_float`, else the video's own frames.
- **In the player** with the real `content.js` (a stand-in worker, a recorded 30 fps video with
  sound, the buffer at 1 s): it drew 98 frames a second on a 180 Hz screen, 92 % of them made
  between, each later in the video than the one before; the moving bar sharp, not doubled.
- **The app:** each frame between is reported (`frame`, `between: true`) at its time in the
  video; its depth is the two frames' blended (`PagePictures.Nearness`, as for frames the model
  skipped), so the model still runs on the video's frames only. `PageVideo.FrameRate` counts
  those only: with the frames between counted, the look-ahead would have held twice as many
  frames as the model gets (`FramesMadeBetweenTheVideosAreNotCountedInItsFrameRate`).
- **The panel:** "Motion: smooth, 60 fps" or "as the video" (`motionNext`,
  `AppSettings.SmoothMotion`). Off by default (user: "60 fps only needed when the source is
  60fps ... If the source is 30fps then do 30"): each video at its own rate, a 60 fps one with
  all its frames, as before.

Not yet seen in the glasses: the depth under a frame between is blended, not moved with the
motion, so a fast edge's depth may lag half a frame there.

**Removed on 2026-10-26** (user: "Does smooth motion do anything? Or it just virtually convert
30fps to fake 60 fps. If that it then remove it entirely to make the app lighter"): it made
frames between, nothing for the 3D. Gone: `motion.js`, the player's frames between, the panel's
Motion line, `AppSettings.SmoothMotion`, the frame report's `between` and its count in
`PageVideo.FrameRate`. Its search lives on in the app as `MotionField` (below).

### Depth smoothed along the motion (2026-10-25, user: "repurpose the motion into temporal smoothing for depth consistency and reducing trailling and flickering")

The depth's per-pixel smoothing mixed each pixel's new depth with its own history at its place.
Where the picture moved, that history belonged to something else, so the filter took the new
depth outright wherever the luma changed (a moving thing flickered as much as the model) and
kept the old one where it did not (a trail where the picture behind looked like the thing).

- **`MotionField`** finds where each pixel was a frame before, from the model input's luma (on
  the post thread, at model size): the search of smooth motion's `motion.js` on the CPU and backward (1/16, 1/8,
  1/4 levels, 3×3 texels, cost for straying and bonus for none, vector median, half then quarter
  texel refinement, median), the levels kept with a 24-texel border so the search reads without
  bounds checks. 4.3–5.4 ms a frame at 756×420 over the cores (20 ms on one).
- **`DepthPostProcessor.FollowMotion`** (on): a pixel's history is the smoothed depth where its
  part of the picture was, and it takes the new depth where the picture there does not match
  (uncovered, a wrong motion) or, where the picture moves within 4 px, where the depth jumped by
  0.1–0.25 or more (`DepthJumpFrom`/`To`): what a moving thing uncovered may look like it. Where
  the picture is still, depth still eases into a change (`StillPixelsSettleSlowly…` holds).
  History is double-buffered now (a pixel reads a neighbour's).
- **Measured** on 150 frames of the sample clip, 756×420, Responsiveness 0.2; flicker along the
  motion: on pixels whose luma moved, |n − n before, where it was| (the motion from a fixed copy
  of the search):

  | | M4 still, at its place → along | along the motion, at its place → along |
  |---|---|---|
  | Distill-Any-Depth Base | 0.0010 → 0.0011 | 0.0026 → 0.0011 |
  | Depth Anything 3 Small | 0.0024 → 0.0025 | 0.0117 → 0.0044 |
  | Video Depth Anything Small | 0.0010 → 0.0010 | 0.0018 → 0.0008 |

  At 0.1 the still flicker falls a third more for every model (0.0007, 0.0016, 0.0008) and
  along the motion 0.0007, 0.0029, 0.0006. The measure shares the search with the filter; the
  synthetic test does not: a textured near object moving 2 px a frame over a textured far
  background, depth noisy ±0.04 (`FollowingTheMotionSmoothsAMovingObjectWithoutATrail`): its
  spread 0.0139 at its place, 0.0070 along the motion (the still background 0.0069); the trail
  in the 8 px behind it 0.140 against 0.002. With white-noise textures the gain was small: a
  quarter-px error makes such a picture unlike itself, so every pixel looked changed; video
  changes smoothly between pixels. Tried and dropped: a per-component median (moving flicker
  0.0011 → 0.0014), unchecked reads (20 → 17 ms on one core, the same over the cores), the jump
  allowed only near found motion (the 0.5-texel motion was missed now and then: trail 0.04).
- **Cost:** post-processing 11.7 ms a frame at 756×420 (from 3.1) and 6.4 at 518×294 (from 2.2),
  alongside the model's 5–11 ms on its own thread: the pair keeps up with 60 fps at 756×420.
  If it ever paces the depth, the motion could be found on the model thread while the GPU runs.

173 tests pass.

### Buffer 2, 3 or 4 s (2026-10-26, user: "set 2S buffer as default and minimum. you can add 3s and 4s")

- **The choices:** Live, Auto, Matched, then Buffer 2 s (the default, as before), 3 s and 4 s;
  0.5 and 1 s are gone. A saved 0.5 or 1 s loads as 2 (`AppSettings.ForgetRemovedChoices`,
  `MinBufferSeconds`/`MaxBufferSeconds`).
- **Room for 4 s:** the player's kept frames may take 800 MB (4 s of 1080p at 60 fps is 746 MB;
  640 before, which would have dropped frames past 3.4 s), and `PagePictures` keeps 300 depth
  frames (240 before: 4 s at 60 fps with nothing over for the look-ahead). The sound's delay line
  is sized to the delay asked (up to 179 s), and the model's look-ahead stays at 30 frames at most.
- **Lighter storage** (user: "Check if there any better way to handle buffer storage"):
  - The player keeps each frame at the smaller of its size on the screen and the video's own: a
    720p video on the 1080p screen at 1280×720, 1.4 MB a frame instead of 3.1 (its chroma is the
    video's own then; the show pass scales as the canvas did). 4 s at 60 fps: 330 MB, not 746.
  - Slots a whole stretch of the delay and a second never needed are given back (`trimSlots`,
    four spare): from 4 s to 2 s, half the memory goes after about 3 s; with the delay off, all
    of it. Before, they stayed until the theater closed. A seek keeps them (refilled within the stretch).
  - The app keeps the depth for the delay in 16 bits a pixel (`NearnessBlend.Pack`; steps of
    1/65535 of the clamped 0..1): 300 frames at 756×420 take 190 MB, not 381. Unpacking or
    blending the frame shown: 0.08–0.46 ms (`ParallelSpeedTests`). The model's own frame goes
    back to it at once (it was held as long as its depth was kept).
  - Room checked on this PC: RTX 5060 with 8 GB (the app about 1.1 GB), 32 GB RAM.
- **The user's run** (Release, 11:57–12:14; 4K, 1440p, 1080p and 720p videos at 24–30 fps):
  the 3 and 4 s buffers held (sound 2.95 and 3.95 s late, about 90 depth frames ahead at 4 s and
  24 fps), VRAM 1.0–1.17 GB, process memory 1.9–2.7 GB, no errors.
- **Fixed: a model change dropped the delay** ("plays the sound live again", then 0.00 s, and the
  whole delay filled again: up to 4 s more of spinner after the load; in the 2026-10-25 run too).
  While a model loads the app asks the player for no frames, and the player reported `pictures:
  false`; the model ready, the app read that as a player that cannot send its frames, went live
  and stopped the delay, before the player's next state said otherwise. `pictures` now says what
  the player can do (WebGL, a video it may read), whether or not frames are wanted now.
  Seen in the next run: 7 model changes with the 4 s buffer, the sound 3.95 s late throughout.

### The virtual monitor off while the app is closed (2026-10-26, user: "when the app is close it disconnect the virtual display")

Nothing shows the virtual monitor while the app is closed: windows, dialogs and the mouse went
there out of sight (the app moved its windows off at exit, new ones could still open there).

- **The driver's own control cannot.** Its pipe (`\\.\pipe\MTTVirtualDisplayPipe`, open to every
  user, UTF-16 commands; `RELOAD_DRIVER`, `SETDISPLAYCOUNT n`, `GETSETTINGS`, `PING`) took
  `SETDISPLAYCOUNT 0`, wrote `<count>0</count>` and restarted, and loaded one monitor again
  ("Loading singular monitor (Monitor Count is not valid)"); put back to 1. Disabling the
  device needs admin (a prompt at every start and exit).
- **So the Windows layout** (the user's choice, against the DesktopSbS rule, now with one
  exception): `Displays/VirtualMonitorSwitch`. At exit, after the windows are moved off, the
  virtual monitor's path is made inactive and the others applied as they are
  (`SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE`), and its
  identity saved (`AppSettings.DisconnectedVirtualMonitor`); not when it is the main display.
  At start, before the layout is read, a path to it from a free source is made active with
  the others and Windows asked for the modes it remembers for that set (`SDC_TOPOLOGY_SUPPLIED`:
  where it was), else the supplied layout with Windows placing it. Kept to try again when it is
  not there (the driver not running). Settings: "Disconnect the virtual monitor while ImmersiveSBS
  is closed" (`DisconnectVirtualMonitor`, on by default).
- **Tested on this PC** (with the user's go-ahead): the switch alone, off and on: the main display
  and the glasses unmoved, the virtual monitor back at (325, −1080). Then the app twice, quit
  with its shortcut: off at exit ("disconnected the virtual monitor VDD by MTT while the app is
  closed"), back at start before the layout was read ("connected … again, where it was"), the
  session on it as before.

### Next

1. The decisions still open in `docs/packaging.md`: the extension in a store, signing, an
   update check, shipping Windows ML's DLL beside GPL-3.0 code.
2. Judge Video Depth Anything Small in the glasses against Distill-Any-Depth Base (above: same
   cost, half the model's flicker, the smoothing evens them on still pixels): keep it, make it
   the theater's default, or give it lighter smoothing (0.35–0.5). Depth Anything 3 flickers
   most for no speed gain at Base: remove it, or keep Small as the fast choice.
3. Video Depth Anything's offline mode in the buffer (the authors' trained mode, above), and
   its states in fp16 (170 MB less VRAM): a new export each.
4. Measures worth taking in a run: M4 flicker for one clip in live, matched and buffer timing;
   the `post … ms` part at 756×420; Soft edges Auto against 1 and 0, Warp Splat against Lookup.
5. From P1, still open: the glasses' refresh rate in 3D mode, and whether the hardware 3D button
   works with the glasses removed from the desktop (plan D2 Stage B).
6. Depth with no delay at all, from the player's own buffered segments (checked feasible on
   YouTube and WeTV, see "Depth ahead from the player's own buffer"): build only if wanted.

## Carried over from the DesktopSbS session

- **Model speed results** are in `docs/video-3d-plan.md` §0 (TensorRT for RTX, fp16, static
  shapes).
- **V0 scratch** (models, `bench.py`, `fix_static.py`, `prep_large.py`, `compare.py`; ~3 GB)
  lives in a temporary Claude scratch folder that may be cleaned up:
  `C:\Users\huyky\AppData\Local\Temp\claude\d--Desktop-DesktopSbS\2fedce75-4706-40db-a5b9-d9423e1c9fd2\scratchpad\v0\`.
  Python venv `winml` there has `onnxruntime-windowsml` 1.30.
- **TensorRT for RTX** comes with Windows (package
  `MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP.2` 2.30.49.0). Provider DLL:
  `C:\Program Files\WindowsApps\MicrosoftCorporationII.WinML.NVIDIA.TRT-RTX.EP.2_2.30.49.0_x64__8wekyb3d8bbwe\ExecutionProvider\onnxruntime_providers_nv_tensorrt_rtx.dll`.
  Registration in Python:

  ```python
  ort.register_execution_provider_library("NvTensorRTRTXExecutionProvider", TRT_DLL)
  dev = [d for d in ort.get_ep_devices() if d.ep_name == "NvTensorRTRTXExecutionProvider"][0]
  so.add_provider_for_devices([dev], {})
  ```

- TensorRT needs static input shapes for these exports (`make_input_shape_fixed`,
  `fix_output_shapes`, shape inference). The available Distill-Any-Depth Large export only
  accepts 518×518; keep only its `output` output before fp16 conversion.
- The full DesktopSbS conversation that led here:
  `C:\Users\huyky\.claude\projects\d--Desktop-DesktopSbS\2fedce75-4706-40db-a5b9-d9423e1c9fd2.jsonl`.
