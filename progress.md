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
