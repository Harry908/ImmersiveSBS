# Progress

The living record of ImmersiveSBS: what each phase changed, how it was verified, and what is
still open. Read it with `docs/plan.md` before starting a phase and update it after.

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

### Next

1. In the glasses: the theater (enter, exit, Play/Pause, cursor alignment), the buffer again
   (sound on the right device; the new audio log during a pause), stretch, tuning panel; then
   tune the defaults; compare the five models.
2. P6 packaging (installer with the driver, model download, README), or P3 once the hardware
   3D button test with the glasses removed from the desktop is done.

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
