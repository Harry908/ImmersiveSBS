# ImmersiveSBS

A full Windows desktop in video glasses running in hardware 3D mode, with optional AI
conversion of 2D video into real stereo 3D.

Glasses such as the RayNeo GT Max show a 3840×1080 side-by-side signal in 3D mode, one
1920×1080 half per eye. A normal desktop on that signal is either squeezed or letterboxed.
ImmersiveSBS instead renders the desktop on a 1920×1080 virtual monitor and shows it, pixel for
pixel, to each eye. When AI 3D is on, a depth model turns 2D video into a true side-by-side
picture.

**Status:** in use and being tested in the glasses; see [docs/plan.md](docs/plan.md) and
[progress.md](progress.md).

## Features

- **The desktop in 3D mode:** the virtual monitor in both eyes; the cursor and stray windows
  are kept where the glasses can show them.
- **AI 3D:** a depth model (Depth Anything V2 Small, Distill-Any-Depth Base, Depth Anything 3
  Small or Base, run by TensorRT for RTX) converts either the browser video or the whole
  screen. Video that is already side-by-side or top-bottom 3D is shown as it is.
- **The browser extension** ([extension/](extension/), Chrome and Edge) is how a video is
  found: it reports exactly where the video is, its frames, pauses, and the captions and
  controls over it. It also delays the video's sound inside the page, at full quality, so the
  picture can be shown later than live with depth for every frame. Without it, only the whole
  screen is converted, and there is no buffer or theater. It sends nothing that says what you
  watch (no titles, addresses or names).
- **Timing:** live, matched (the video just late enough for its depth), or a 0.5–2 s buffer
  (needs the extension).
- **3D theater:** a "3D theater" button over a browser video opens it in a player that fills
  the glasses. The player has a seek bar, the time, 10 s back and forward, play/pause, mute,
  and a 3D settings panel. Keys: Space or K play/pause, ←/→ or J/L skip 10 s, Esc or a double
  click leave.

## Requirements

- Windows 10 2004 or later (Windows 11 recommended), a DirectX 11 GPU, and a virtual display
  driver for the 1920×1080 virtual monitor.
- For AI 3D: an NVIDIA RTX GPU and the model files in `%LOCALAPPDATA%\ImmersiveSBS\models`.
- Protected video (Netflix, Disney+, Prime and similar) is not supported.

## The browser extension

1. Open `edge://extensions` (or `chrome://extensions`) and turn on **Developer mode**.
2. **Load unpacked** and pick the `extension` folder next to `ImmersiveSBS.exe`
   (Settings → Buffer → **Open extension folder**).
3. After an update, reload the extension and the tabs that play video.

Its badge says **off** (app not reachable), **on** (connected) or **3D** (the app follows the
video in this tab). See [extension/README.md](extension/README.md).

## Building

```
dotnet build ImmersiveSBS.slnx -c Release
dotnet test ImmersiveSBS.slnx
```

Requires the .NET 10 SDK. Use the Release build in the glasses: a Debug build runs the depth
post-processing several times slower.

## Licence

GPL-3.0 (see [LICENSE](LICENSE)). Parts are ported from
[DesktopSbS](https://github.com/Harry908/DesktopSbS), also GPL-3.0.
