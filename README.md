# ImmersiveSBS

A full Windows desktop in video glasses running in hardware 3D mode, with optional AI
conversion of 2D video into real stereo 3D.

Glasses such as the RayNeo GT Max show a 3840×1080 side-by-side signal in 3D mode, one
1920×1080 half per eye. A normal desktop on that signal is either squeezed or letterboxed.
ImmersiveSBS instead renders the desktop on a 1920×1080 virtual monitor and shows it, pixel for
pixel, to each eye. When AI 3D is on, a depth model turns 2D video into a true side-by-side
picture.

**Status:** early (phases P1–P2: tray app, not yet field-tested); see [docs/plan.md](docs/plan.md) and
[progress.md](progress.md).

- Requirements (planned): Windows 10 2004 or later (Windows 11 recommended), a DirectX 11 GPU;
  an NVIDIA RTX GPU for AI 3D.
- Protected video (Netflix, Disney+, Prime and similar) is not supported.

## Building

```
dotnet build ImmersiveSBS.slnx -c Debug
```

Requires the .NET 10 SDK.

## Licence

GPL-3.0 (see [LICENSE](LICENSE)). Parts are ported from
[DesktopSbS](https://github.com/Harry908/DesktopSbS), also GPL-3.0.
