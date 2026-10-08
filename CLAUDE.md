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
```

.NET 10 SDK, SDK-style project, x64 only, target `net10.0-windows10.0.19041.0` (WinRT APIs such
as Windows.Graphics.Capture come from the target framework). Per-monitor v2 DPI awareness is set
in `app.manifest`: every coordinate is a physical pixel.

## Rules carried over from DesktopSbS

- Do not launch the app without asking: it takes over the glasses.
- Do not install or remove the virtual display driver, change display settings or scaling, or
  call `SetDisplayConfig`. The user changes the display layout in Windows Settings.
- Do not kill the user's running session; if the exe is locked, build another configuration.
- Harnesses and experiments are throwaway and stay out of the repo (use the scratchpad).
- Commit only when asked.
- DRM-protected content is out of scope.
- Ported DesktopSbS files keep a header line naming the original file.
