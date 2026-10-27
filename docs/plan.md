# ImmersiveSBS — plan

Status (2026-10-20): **P0–P2, P4 and P5 done and in daily use in the glasses; P3 not started; P6
(packaging): the one-click installer builds, not yet released** (`docs/packaging.md`; details in
`progress.md`). Written 2026-10-07. Two
things changed from this plan on the way: sound is delayed only inside the browser page, by the
extension, never captured by the app (D5 below is superseded), and browser video reaches AI 3D
only through the extension's 3D theater (`docs/extension-first-plan.md`).

ImmersiveSBS turns a pair of video glasses in hardware 3D mode (RayNeo GT Max: 3840×1080, one
1920×1080 half per eye) into a full desktop monitor, and can convert 2D video on that desktop
into real stereo 3D with a depth model. It replaces the overlay design of
[DesktopSbS](https://github.com/Harry908/DesktopSbS) (same author, GPL-3.0), which places DWM
thumbnails over the glasses and has reached its limits:

- A 3840×1080 desktop cannot fill two 16:9 eyes: Keep ratio gives black bars, otherwise the
  picture is stretched.
- The Start menu and Alt+Tab cannot be thumbnailed.
- When the glasses are both source and destination, the overlays fight Windows: z-order
  repairs, a cursor-scheme registry swap to hide the real cursor, and hotkey and black-screen
  bugs.

`video-3d-plan.md` is the earlier plan for AI video conversion inside DesktopSbS. Its model
measurements, pipeline, video detection, depth post-processing and stereo synthesis (§0, §4–§7)
carry over. Its process split, overlay presentation and DesktopSbS integration (§2, §3 D1/D2/D6/D7,
§8–§10) are superseded by this document.

## 1. Scope

**In scope**

1. **Desktop mode.** One 16:9 desktop, 1920×1080 by default, shown pixel for pixel to each eye
   while the glasses are in hardware 3D mode. Both eyes see the same picture.
2. **2D passthrough.** When the glasses are in 2D mode (1920×1080), the same desktop is shown
   1:1, so the user always works on one desktop and 2D/3D is only how it is displayed.
3. **AI 3D mode.** When switched on, a depth model estimates per-pixel depth, two views are
   synthesized, and the glasses show true full side-by-side 3840×1080.
4. **Buffered video.** For recorded video (not live streams), the converted video may be held
   back 1–2 s for better depth (look-ahead smoothing, larger models), with the browser's sound
   delayed by the same amount.
5. **Native 3D video.** Video that is already side-by-side or top-bottom is shown as such.

**Out of scope**

- DRM-protected video (PlayReady/Widevine hardware paths). It captures as black, or refuses to
  play on a virtual monitor; nothing here tries to change that. The user can make the glasses an
  ordinary monitor in Windows settings to watch it.
- Several virtual screens, head tracking, screens anchored in space (Viture SpaceWalker style).
- Browser extensions, our own display or audio drivers.
- Changing display settings from the app. The user positions and enables monitors in Windows
  Settings; the app reads the layout (DesktopSbS rule: never call `SetDisplayConfig`). One
  exception since 2026-10-26: the virtual monitor is taken off the desktop while the app is
  closed and put back where it was when it starts (`Displays/VirtualMonitorSwitch`).

## 2. Architecture

```
Apps, browser, Start menu
      │  Windows composes the desktop as usual
      ▼
Virtual monitor "ImmersiveSBS" (1920×1080, VirtualDrivers IddCx driver, rendered on the RTX)
      │  capture: GPU texture per frame + cursor shape/position
      ▼
ImmersiveSBS.exe (.NET 10, x64)
  ├─ desktop mode  : frame → both halves                       (~1 frame added)
  ├─ AI 3D mode    : video area → depth → two views; rest flat (or whole frame)
  │                  optional 0.5–2 s buffer, sound delayed equally
  └─ cursor drawn last, from the captured cursor shape, at zero depth
      │
      ▼
Glasses (3840×1080 in 3D, 1920×1080 in 2D): one full-screen presenter, nothing else
```

Keyboard and mouse are Windows' own: the user works on the virtual monitor as on any monitor.
Nothing is overlaid on top of real windows, so there is no click-through, no z-order and no
cursor hiding.

## 3. Decisions

### D1. Source: a virtual monitor from the VirtualDrivers driver

[VirtualDrivers/Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver):
an IddCx (user-mode) indirect display driver, MIT, SignPath-signed, configurable resolutions,
refresh rates and render GPU.

- Default 1920×1080, the eye size. 2560×1440 (more space, softer) and 1280×720 are options.
- Refresh rate set to the glasses' 3D refresh rate to avoid judder (measure in P1).
- Render GPU set to the adapter that drives the glasses (the RTX 3060 on the development
  machine), so frames never cross adapters.
- Rejected: our own driver (hands frames over without a capture step, ~1 frame less latency,
  but driver development and Microsoft attestation signing); a physical second monitor as
  source (works without a driver, but the glasses could not be used alone); reserving half of
  the glasses' own 3840×1080 desktop (windows, the cursor and full-screen apps leak into the
  other half; the taskbar spans the full width).

### D2. Presentation on the glasses: two stages

- **Stage A (P1):** the glasses stay an extended monitor; the app covers it with one
  borderless full-screen window (flip-model swap chain, `DXGI_SWAP_EFFECT_FLIP_DISCARD`,
  tearing allowed off). The monitor is placed in the layout so the cursor rarely reaches it;
  stray windows are moved back. Quick, slightly leaky.
- **Stage B (P3):** the user enables Windows 11 "Remove display from desktop" for the glasses
  (specialized display). Windows then never places windows or the cursor there, and the app
  drives the glasses through `Windows.Devices.Display.Core` (`DisplayManager`, the API VR
  runtimes use). Airtight. **Open:** does the glasses' hardware 3D button still switch to
  3840×1080 while removed from the desktop? P1 tests this before Stage B is committed to.

### D3. Capture: DXGI Desktop Duplication first, Windows.Graphics.Capture as fallback

- Desktop Duplication gives the frame as a GPU texture, dirty rectangles, and the cursor shape
  and position separately, so the cursor can be drawn after the AI pass at zero depth. It is the
  usual way to capture IddCx monitors (Sunshine does).
- Windows.Graphics.Capture is the fallback (`IsBorderRequired = false`,
  `IsCursorCaptureEnabled = false`).
- P1 measures both: added latency, GPU cost, behaviour on mode changes and the secure desktop.

### D4. Inference: ONNX Runtime with TensorRT for RTX

Unchanged from `video-3d-plan.md` §0 and D3/D4: TensorRT for RTX through the Windows ML
package, fp16, static input shapes, one cached engine per model and size. DirectML measured
~65× slower on this machine and is not a fallback worth shipping for NVIDIA. Model ladder:
Depth Anything V2 Small (Apache-2.0, fast), Distill-Any-Depth Base (MIT, default), Large (MIT,
518×518 export today; 518×294 re-export planned).

### D5. Sound: process loopback, turned-down session, delayed playback

> Superseded on 2026-10-11: the app no longer captures or replays any sound. The browser
> extension delays the theater video's sound inside the page through Web Audio, and since
> 2026-10-19 the page delays the picture too (`progress.md`, "Two layers").

Only needed when the video is buffered.

1. Capture the browser's process tree with process loopback
   (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, Windows 10 2004+).
2. Silence the live sound without silencing the capture. **Measured in P5** (Windows 11 build
   26300, `AudioLoopbackTests`): loopback is taken *after* the session mute (a muted session
   captures silence) and after the session volume, which acts as amplitude v² down to v = 0.001
   (−120 dB; 0.0001 is silent). So the browser's sessions are turned down to v = 0.01 (−80 dB,
   inaudible) instead of muted, and the capture is boosted by (1/v)² = 10 000; the mixer works
   in float, so nothing is lost. A boosted packet with an implausible peak came from a session
   not yet turned down and passes at unity gain.
3. Play the captured sound through WASAPI on the user's output (the glasses carry sound over
   DisplayPort: "SmartGlasses (NVIDIA High Definition Audio)") exactly the buffer length after
   capture, at the browser's own session volume.
4. Video frames and audio packets are both stamped with QPC; each is presented at
   `capture time + buffer`. Capture and playback share the device clock, so there is no drift
   to resample; packet jitter is absorbed and larger slips resynchronise.
5. The browser's volumes are restored on stop, exit and crash; a marker file lets the next
   start undo a killed run (as DesktopSbS does for the cursor scheme).

Fallbacks, in order: route the browser to an unheard output and capture that; a virtual audio
cable (driver); none of these is planned.

### D6. Stack

- .NET 10, x64, WPF for tray, settings and notices (the Windows 11 theme ports from DesktopSbS).
- Direct3D 11 through Vortice.Windows; compute shaders for the eye split, warp and hole fill.
- WinRT APIs through the `net10.0-windows10.0.19041.0` target.
- Shipped as a folder or self-contained build with an installer that also installs the
  virtual display driver.

### D7. Code ported from DesktopSbS (GPL-3.0)

Display detection and identity (`DisplayConfigurationService`, `DisplayIdentity`), 3D-mode
detection (`DisplayStereoStateEvaluator`, `KnownGlasses`), `DisplayChangeMonitor`,
`GlobalKeyboardHook` (dedicated hook thread), `Log`, the tray, the Windows 11 theme. Ported files
keep their history note in the header and are adapted to nullable, SDK-style .NET 10.

## 4. Phases

### P0 — Repo (done)

Skeleton, licence, plans.

### P1 — Prototype: virtual monitor to both eyes (M)

Throwaway-quality code, but in the repo.

- Install the virtual display driver (with the user's go-ahead), 1920×1080, render GPU = RTX.
- Capture it (Desktop Duplication; WGC behind a switch).
- Present full-screen on the glasses (Stage A), the frame drawn into both halves; 1:1 in 2D mode.
- Draw the cursor from the captured shape.
- Measure: added latency (photodiode-free: QPC from capture to present, plus a flashing test
  pattern filmed with a phone through the lens), GPU cost, VRAM, text sharpness.
- Test: hardware 3D button with the glasses removed from the desktop (D2 Stage B question).

**Exit:** the user works a full session on the virtual monitor through the glasses and signs
off on sharpness and latency.

**Status:** done; Desktop Duplication is the default capture. The hardware 3D button test with
the glasses removed from the desktop (the Stage B question) was never run.

### P2 — App shell (M)

Tray, settings, shortcuts (ported hook), auto-start when the glasses enter 3D mode (ported
state evaluator), hot-plug and mode changes, driver missing or disabled, logging. Stray-window
and cursor handling for Stage A.

**Status:** done (tray, settings, shortcuts, guards, the window on a real monitor when the
glasses do not show the virtual monitor).

### P3 — Stage B presentation (M)

`DisplayManager` ownership of the glasses, if P1 showed the 3D button still works. Otherwise
Stage A stays and this phase hardens it.

**Status:** not started; Stage A works well enough that nothing has asked for it.

### P4 — AI 3D mode (L)

The V3–V6 work of `video-3d-plan.md` on the captured frame: video-area detection (§5), depth
inference (§0, D4), post-processing (§6), stereo synthesis (§7), native 3D video (§7.4). Default
target is the video area only; whole-frame conversion is an option (text and menus shimmer
slightly with depth).

**Status:** done, then reworked by `docs/improvement-plan.md`. The video area is no longer
detected: the extension's theater says where the video is.

### P5 — Buffered video and sound (M)

Buffer setting 0 / 0.5 / 1 / 2 s (default 1 s for recorded video). Look-ahead temporal
smoothing, larger model tiers. D5 audio. Exit: lip-sync within ±20 ms over a 2-hour film.

**Status:** done, differently: the browser page delays both picture and sound (default 2 s),
holds them through a pause and restarts them at a seek; the app shows the screen as it is and
takes depth from the frames the page sends ahead.

### P6 — Packaging (S)

Installer (app + driver), README, update check.

**Status:** the installer builds (2026-10-20): one click, the app with the default model and
the Virtual Display Driver when it is missing (`packaging/build-installer.ps1`); a portable
package too (`packaging/publish.ps1`); a version number, third-party notices, the README. Still
to do: a run end to end on a PC without the driver, other models' distribution, the extension
in a store or not, signing and the update check (`docs/packaging.md`).

## 5. Open questions

1. ~~Desktop Duplication or Windows.Graphics.Capture (P1 measures).~~ Desktop Duplication;
   Graphics Capture stays as a fallback in the settings file.
2. Glasses refresh rate in 3D mode, and whether the hardware 3D button works with the glasses
   removed from the desktop (P1). Still open.
3. ~~Whole-frame AI as well as video-only (P4; video-only is the default).~~ Both: the theater,
   and the whole screen as a setting.
4. ~~Does process loopback really tap before the session mute (P5)?~~ No: after the mute and after
   the session volume (v²); D5 turns the session down instead of muting.

## Sources

- [Virtual Display Driver (VirtualDrivers)](https://github.com/VirtualDrivers/Virtual-Display-Driver)
- [Indirect display driver overview](https://learn.microsoft.com/en-gb/windows-hardware/drivers/display/indirect-display-driver-model-overview)
- [Windows.Devices.Display.Core](https://learn.microsoft.com/en-us/uwp/api/windows.devices.display.core)
- [Desktop Duplication API](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api)
- [AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS](https://learn.microsoft.com/en-us/windows/win32/api/audioclientactivationparams/ns-audioclientactivationparams-audioclient_process_loopback_params)
- [AUDCLNT_STREAMOPTIONS (loopback before or after volume and mute)](https://github.com/MicrosoftDocs/sdk-api/blob/docs/sdk-api-src/content/audioclient/ne-audioclient-audclnt_streamoptions.md)
