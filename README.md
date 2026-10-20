# ImmersiveSBS

A full Windows desktop in video glasses running in hardware 3D mode, with AI conversion of 2D
browser video into real stereo 3D.

Glasses such as the RayNeo GT Max show a 3840×1080 side-by-side signal in 3D mode, one
1920×1080 half per eye. A normal desktop on that signal is either squeezed or letterboxed.
ImmersiveSBS renders the desktop on a 1920×1080 virtual monitor and shows it, pixel for pixel,
to each eye. In its 3D theater, a depth model turns a 2D browser video into a true side-by-side
picture.

**Status:** version 1.0.0, in daily use on the development setup; packaging is prepared
([docs/packaging.md](docs/packaging.md)). The work so far is recorded in [progress.md](progress.md).

## What it does

- **The desktop in 3D mode.** While the glasses are in 3D mode, the virtual monitor fills both
  eyes. The mouse and stray windows are kept on the virtual monitor, where you can see them.
  When the glasses are off or in 2D mode, the virtual monitor shows in a window on your main
  screen instead, so nothing on it is ever out of reach.
- **The 3D theater.** A "3D theater" button appears over a browser video. It opens the video
  alone, full screen, and shows it in 3D with depth from an AI model (Distill-Any-Depth Base by
  default; Depth Anything V2 Small, Depth Anything 3 Small and Base, and Video Depth Anything
  Small as a trial). The page's subtitles come along and are kept flat and readable.
- **No delay you can see, no gaps.** For the best depth the theater can play a video up to two
  seconds late, picture and sound together, while the model works ahead. Pause, play and seek
  keep picture and sound in step; the controls always answer at once.
- **Video that is already 3D** (side-by-side or top-bottom) can be shown as it is.
- **The whole screen in 3D** is a setting, for games or slideshows (text and menus shimmer a
  little with depth).

## Requirements

- Windows 11 24H2 or later, and an NVIDIA GeForce RTX GPU for AI 3D (it runs on TensorRT for
  RTX, which Windows ML installs). Without one the desktop still shows in the glasses, flat.
- Video glasses with a side-by-side 3D mode (3840×1080).
- The [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) for the
  1920×1080 virtual monitor; the installer installs it.
- Edge or Chrome, for the 3D theater.
- Protected video (Netflix, Disney+, Prime Video and similar) cannot be shown in the theater.

## Getting started

1. Run `ImmersiveSBS-Setup-<version>.exe` and allow it to make changes. It installs the app, the
   default depth model and the Virtual Display Driver (with its monitor set to 1920×1080, if the
   driver is not installed already), and starts the app. Its icon sits in the notification area.
2. On the last page it opens Edge's extensions page and the extension's folder: turn on
   **Developer mode**, choose **Load unpacked** and pick that folder (later: Settings → **Open
   extension folder**; in Chrome, `chrome://extensions`). The extension's toolbar badge reads
   **on** while the app is running.
3. Switch the glasses to 3D mode. The desktop appears in both eyes. Arrange the virtual monitor
   in Windows display settings like any other screen if you like.
4. The first use of a model prepares it for your GPU, which takes about 20 seconds. Settings
   (from the tray icon) shows the displays it found, the model, and the browser connection.

Without the installer, unpack the portable zip anywhere, install the Virtual Display Driver
yourself with a 1920×1080 monitor, and run `ImmersiveSBS.exe`.

Models live in the `models` folder next to the app, or in `%LOCALAPPDATA%\ImmersiveSBS\models`.

## Everyday use

**The tray icon** has the everyday commands: Pause, Whole screen in 3D, 3D theater, Show in a
window, Settings, Tuning (the depth controls, live) and Exit.

**In the theater,** move the mouse to show the bar: the seek bar and time, 10 s back and
forward, play and pause, mute, the **3D** panel and Exit. A click on the picture plays or
pauses. Keys: Space or K play/pause, ← → or J L skip 10 s, M mute, Esc leaves. The 3D panel
changes the model, resolution, depth, screen plane, timing, stretch and whether the video is
2D or already 3D.

**Timing** decides how the picture and its depth meet:

| Setting | What happens |
|---|---|
| Live | No delay. Depth may trail fast motion by a frame or two. |
| Auto | Live, until depth falls behind; then Matched. |
| Matched | Picture and sound are delayed just enough that every frame has its own depth. |
| Buffer 0.5 / 1 / 2 s | Picture and sound play that much later, and the model looks ahead for steadier depth. Opening the theater and seeking show a spinner while it fills. The default is 2 s. |

**Keyboard shortcuts** (change them in Settings):

| Keys | Action |
|---|---|
| Ctrl+Alt+B | Pause or resume |
| Ctrl+Alt+D | AI 3D on or off |
| Ctrl+Alt+X / Ctrl+Alt+W | More / less depth |
| Ctrl+Alt+PageUp / PageDown | Picture further forward / back |
| Ctrl+Alt+T | Tuning panel |
| Ctrl+Alt+F1 | Settings |
| Ctrl+Alt+C | Exit |

## Privacy

The browser extension talks only to the app on this PC, over `127.0.0.1`. It sends where the
video is on the screen, its timing, the boxes of the player's controls and subtitles, and small
copies of the video's frames for the depth model. It never sends page titles, web addresses,
video names or subtitle text, and the app keeps none of it. See
[extension/README.md](extension/README.md).

## When something goes wrong

- The log is `%LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log`. Its `Stats:` line every 10 seconds
  shows the frame rates, the depth timing and the GPU's work.
- Settings shows what the app sees: the displays it found, the model's state, the browser
  connection.
- After an update, reload the extension (its card in `edge://extensions`) and the tabs that
  play video.

## Building from source

```
dotnet build ImmersiveSBS.slnx -c Release
dotnet test ImmersiveSBS.slnx
.\packaging\publish.ps1 -Models dad-base     # the portable package, in artifacts\
.\packaging\build-installer.ps1              # the one-click installer (needs Inno Setup 6)
```

Needs the .NET 10 SDK. Use the Release build in the glasses: a Debug build runs the depth
post-processing several times slower. The model files are not in the repository.

**Documents:** [docs/code-map.html](docs/code-map.html) maps the code with diagrams;
[docs/plan.md](docs/plan.md) is the design and phases; [docs/packaging.md](docs/packaging.md)
covers building and shipping a release; [CLAUDE.md](CLAUDE.md) holds the architecture notes
and the lessons learned.

## Licence

GPL-3.0 (see [LICENSE](LICENSE)). Parts are ported from
[DesktopSbS](https://github.com/Harry908/DesktopSbS), also GPL-3.0. The libraries and models it
uses are listed with their licences in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
