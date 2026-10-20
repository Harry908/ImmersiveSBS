# Live 2D-to-3D video conversion — plan

> Carried over from DesktopSbS, where it was written for the overlay design. `plan.md` says
> which parts still apply (§0 and §4–§7) and which are superseded.

Status (2026-10-19): **historical.** Its model work (§0), pipeline (§4), post-processing (§6) and
stereo synthesis (§7) were built in ImmersiveSBS P4 and since reworked by `improvement-plan.md`;
the rest was written for DesktopSbS. The original status follows.

Status: **plan only, nothing implemented.** Written 2026-09-29, revised 2026-10-07 (stronger
models, TensorRT first, no extension, picture-in-picture), against `feature/win11-rayneo` at
`163f755`. Milestone 4 in `progress.md` terms; phases are numbered V0–V9 so they do not collide
with Phases 1–10.

Scope in one sentence: while a video plays in Edge or Chrome on the source display, DesktopSbS
shows that video in the glasses as real stereo 3D, with per-pixel depth estimated live by a
neural network, while everything else keeps working exactly as today.

DRM-protected video (Netflix, Disney+, Prime) is out of scope. It captures as black and nothing
here tries to change that.

## 0. Target machine (queried 2026-09-30)

DXGI enumeration of the development machine:

| Adapter | LUID | Dedicated VRAM | Drives |
|---|---|---|---|
| 0: NVIDIA GeForce RTX 3060 Laptop GPU (Ampere) | `00000000-02735B9F` | 5994 MB | Acer EDB323QU (`DISPLAY10`, 2560×1440) and the GT Max (`DISPLAY9`, 3840×1080 in 3D) |
| 1: Intel Iris Xe | `00000000-00014EE5` | 128 MB | the built-in panel (`DISPLAY1`) when it is on |
| 2: Microsoft Basic Render Driver | | | none |

Consequences for this plan:

- **One GPU for everything.** Capture, inference and presentation all sit on the RTX 3060,
  because the glasses and the usual source display are both wired to it. There is no
  cross-adapter traffic. The hybrid-GPU design (§4.3) stays for other laptops, and for when the
  built-in panel is the source.
- **Tensor cores.** fp16 inference runs on them, through DirectML metacommands or TensorRT.
- **Windows ML with TensorRT for RTX is available.** The machine runs Windows 11 build 26300
  (24H2+ is required). TensorRT for RTX is Windows ML's default execution provider on NVIDIA,
  supports Ampere (RTX 30xx) and later, and NVIDIA reports it over 50% faster than DirectML. It
  moves from "optional later" to a V0 measurement (D4).
- **6 GB VRAM** is shared with the browser, DWM and the 3840×1080 glasses surfaces. The engine
  budget is **≤ 500 MB**, measured in V0.
- **Laptop power.** The dGPU is awake anyway while the glasses are connected, since it drives
  them. On battery it clocks down hard, so the battery policy (§8.3) matters here.
- **Measured (V0 G2, 2026-10-07).** TensorRT for RTX 2.30 (Windows ML package, already
  installed by Windows), ONNX Runtime 1.30, fp16 weights, static input shapes, warm p50 over
  100 runs; GPU memory is the session's own:

  | Model | 392×224 | 518×294 | 756×420 | 518×518 | GPU memory | Engine build |
  |---|---|---|---|---|---|---|
  | Depth Anything V2 Small | 2.9 ms | 3.8 ms | 6.9 ms | | 180–220 MB | 12–17 s |
  | Distill-Any-Depth Base | 4.8 ms | 8.0 ms | 17.0 ms | | 330–400 MB | 16–18 s |
  | Distill-Any-Depth Large | | | | 40.5 ms | 883 MB | 26 s |

  - **DirectML is out on this machine:** Small at 518×294 took 250–290 ms (the GPU stayed near
    50% use and low clocks), barely ahead of the CPU at 368 ms. TensorRT is about 65× faster.
  - **Static shapes are required:** with dynamic input sizes TensorRT cannot build an engine
    for these exports (`Reshape placeholder has no corresponding input dimension`). One engine
    per tier and size, built once and cached (D4), is mandatory, not an optimization.
  - **Large:** the available export has 518×518 positional embeddings baked in, so only
    518×518 works (a 16:9 frame is squeezed into it). 40.5 ms fits 24 fps, not 30 or 60, and
    it takes 883 MB, over the 500 MB budget. A re-export at 518×294 from the original weights
    (777 tokens instead of 1369) should come in around 23 ms and 600 MB; that is a V3 task.
  - **Base at 518×294 (8 ms) fits 60 fps;** at 756×420 (17 ms) it fits 30 fps.
  - Depth maps on a test photo (`compare.png` in the V0 scratch folder): Large separates the
    windows and the through-door room best and keeps chair legs; Base is close; Small is
    smoother and loses thin structure.

---

## 1. Goals, non-goals, definition of done

**Goals**

1. Per-pixel depth for browser video (Edge and Chrome by default, user-editable list), live, at
   the video's own frame rate (24–60 fps).
2. Works in every existing mode: Full/Half side-by-side, top-bottom, Keep ratio, Sharp 1:1,
   source = destination, any DPI mix, hot-plug.
3. Fullscreen video, theater mode and ordinary in-page video.
4. Comfortable by default. Total disparity stays inside the field-of-view comfort budget that
   already governs window depth (math.md §5.4).
5. Graceful degradation. When anything is uncertain (no video found, window moving, engine
   crashed, GPU too slow), the video falls back to today's flat DWM thumbnail. The feature can
   only add, never break the desktop.
6. Video that is already side-by-side or top-bottom 3D is detected and shown as native 3D.

**Non-goals (this milestone)**

- DRM content.
- More than one converted video at a time. The largest wins; the others stay flat.
- Converting whole windows or the desktop. Only the video rectangle is converted.
- Audio delay. The browser's audio cannot be delayed from outside; the design keeps added video
  latency small instead (§4.4).
- Firefox and desktop players. They work the moment they are added to the app list, but are not
  in the default list or the test matrix.

**Done when**

- All V-phase exit criteria below pass.
- A 30-minute soak (YouTube playlist, fullscreen and windowed) shows no leak, crash, or fallback
  loop.
- The user has watched the clip set (§11.3) in the GT Max and signed off on comfort and quality.

---

## 2. Architecture overview

```
DesktopSbS.exe  (.NET 4.8, WPF — role unchanged)        DesktopSbS.Video.exe  (.NET 10, x64)
────────────────────────────────────────────────        ─────────────────────────────────────
render thread (20 ms passes)                              capture   Windows.Graphics.Capture of
  enumerate windows, depth levels, thumbs                           the target browser window
  pick the target video window ───── Scene ─────▶        detect    video rectangle, letterbox
  stack the engine's overlay window                      depth     Distill-Any-Depth Base/Large,
  show engine status        ◀──── Status ──────                    TensorRT (Windows ML) or DirectML
                                                         post      normalize, temporal filter,
tray / shortcuts / Settings ──── Settings ─────▶                   upsample, static overlays
                                                         stereo    per-eye warp + hole fill
                                                         present   one transparent, click-through
                                                                   overlay window on the
                                                                   destination (DirectComposition)
```

The overlay window sits in the z-order exactly where the browser's thumbs are, one step in front
of them. It covers the video area of the browser's thumbs with the converted picture. Windows in
front of the browser stay in front of the video. When the engine hides its overlay, the flat
thumb underneath simply shows through.

---

## 3. Key decisions

Each decision names the alternatives that were rejected and why.

### D1. A separate engine process, .NET 10, x64

The conversion runs in a new executable, `DesktopSbS.Video.exe` (SDK-style project,
`net10.0-windows10.0.22621.0`, x64, self-contained), launched and supervised by DesktopSbS.

- **Crash isolation.** GPU driver resets, native ONNX Runtime faults and device-removed errors
  cannot take down desktop rendering. That matters more than usual here, because a DesktopSbS
  crash while cursors are hidden is costly (`RecoverAfterCrash`).
- **Modern, maintained interop.** Vortice.Windows 3.x (D3D11, DXGI, DirectComposition) targets
  net9/net10 only. `Windows.Graphics.Capture` is first-class with a Windows TFM. None of this is
  comfortable on .NET Framework 4.8 (SharpDX is abandoned; hand-written COM interop for D3D11 +
  DComp + WinRT capture is thousands of lines).
- **Size stays out of the core app.** Users who never convert video do not load ~150 MB of
  runtime, DirectML and model.
- **.NET 10, not 8.** .NET 8 support ends November 2026; .NET 10 is LTS to November 2028.

*Rejected:*

- **In-process on .NET 4.8.** Loses crash isolation and needs unmaintained or hand-written
  interop.
- **Retargeting the whole app to .NET 10.** A large unrelated migration with WPF/WinForms risk,
  and not needed for this feature.
- **C++ engine.** A second language and toolchain for one feature.

### D2. Capture with Windows.Graphics.Capture, per window

`GraphicsCaptureItem` for the target browser `HWND`, with a free-threaded
`Direct3D11CaptureFramePool` (BGRA8, 2 buffers), `IsCursorCaptureEnabled = false`, and
`IsBorderRequired = false` after `GraphicsCaptureAccess.RequestAccessAsync(Borderless)`.

- It captures the browser's own surface, so other windows on top, our overlays and the cursor are
  never in the picture. There is no feedback loop when source = destination.
- It fires only when the window content changes, so a paused video costs nothing.
- It is GPU-to-GPU; the full-resolution frame never touches the CPU.

**Risk.** Borderless capture is documented for packaged apps (`graphicsCaptureWithoutBorder`
capability). Unpackaged apps such as OBS capture without a border on Windows 11 in practice.
Gate G1 in V0 verifies this.

*Fallbacks, in order:*

1. Give the engine package identity with a sparse package (needs a signing certificate).
2. Desktop Duplication of the source output, cropped to the video rectangle, with our overlays
   excluded via `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`. This loses occlusion
   correctness when something overlaps the video.

### D3. Depth model: a ladder, Distill-Any-Depth by default

The user wants a strong model, not the smallest one. The ladder, all relative inverse depth
(larger = nearer, affine-invariant; §6 turns it into stable nearness), all ONNX:

| Tier | Model | Params | Licence | Role |
|---|---|---|---|---|
| Quality | **Distill-Any-Depth Large** | 300 M | MIT | default on fast GPUs for 24/30 fps; shipped |
| Balanced | **Distill-Any-Depth Base** | 97.5 M | MIT | default for 60 fps and mid GPUs; shipped |
| Fast | Depth Anything V2 Small | 25 M | Apache-2.0 | integrated GPUs, battery; shipped |
| Own model | Depth Anything V2 Large, Video Depth Anything Large, any compatible ONNX | 335 M | CC-BY-NC-4.0 | **never shipped**; the user can point Settings at a file they downloaded, for personal use |

- **Licences.** MIT and Apache-2.0 are compatible with GPLv3, so the three shipped tiers go in
  the installer component. Depth Anything V2 Base/Large/Giant and Video Depth Anything Large are
  non-commercial and cannot be bundled with a GPL app.
- **Model contract.** Input `1×3×H×W` RGB, ImageNet-normalized, H and W multiples of 14; output
  `1×H×W` (or `1×1×H×W`) relative inverse depth. A user model must match it; Settings validates
  the input and output shapes when the file is chosen.
- **Temporal consistency** comes from §6 for every tier. Video Depth Anything (Small is
  Apache-2.0; streaming mode since July 2025) stays an upgrade candidate behind the same
  interface if V0 finds a streaming export that fits the budget.

*Rejected:*

- **Depth Pro and other metric-depth models.** Far too slow for live video.
- **Diffusion-based depth (Marigold and similar).** Seconds per frame.
- **Heuristic depth** (bottom-near gradient, TV-style). Kept only as the "no GPU" fallback.

### D4. Inference runtime: ONNX Runtime with the DirectML execution provider

`Microsoft.ML.OnnxRuntime.DirectML` runs on any DirectX 12 GPU (NVIDIA, AMD, Intel) on
Windows 10 1903+.

- **DirectML is in maintenance mode.** It gets security fixes only; new work goes to Windows ML
  (Windows 11 24H2+), which exposes the same ONNX Runtime API and picks vendor execution
  providers (TensorRT for RTX, OpenVINO, and so on).
- So inference sits behind `IDepthEstimator`, with two implementations:
  - **`WinMLEstimator`.** Windows ML, on Windows 11 24H2+. Windows downloads and updates the
    vendor execution providers itself; on this machine that is TensorRT for RTX (§0), which is
    not shipped by us.
  - **`OnnxDmlEstimator`.** ONNX Runtime + DirectML: every other machine, and the fallback if the
    Windows ML provider is unavailable or fails.
- **Choice at start-up.** On NVIDIA RTX with Windows 11 24H2+ (this machine), Windows ML with
  TensorRT for RTX is the first choice; NVIDIA reports it over 50% faster than DirectML, and the
  larger tiers depend on that margin. Otherwise DirectML. The Settings status line shows which
  one ran.
- **Static shapes.** One optimized engine per tier and preset, built for that exact input size:
  TensorRT is fastest with fixed shapes, and presets change rarely.
- **TensorRT builds an engine per GPU on first run** (seconds). Cache it under
  `%LOCALAPPDATA%\DesktopSbS\Video\cache`. The first conversion shows "preparing" instead of
  stalling.
- **V0 confirms** that Windows ML works from an unpackaged, self-contained exe (Windows App SDK
  bootstrap), and measures both paths on the RTX 3060.

*Rejected:*

- **Bundling TensorRT ourselves.** NVIDIA only, gigabytes of DLLs. Windows ML delivers the same
  provider without us shipping it.
- **CPU.** Far too slow for video.

### D5. Small tensors cross the CPU; frames never do

The captured frame is cropped, resized and normalized on the GPU (D3D11 compute) to the model
input (for example 518×294×3), then read back.

- The round trip is about 0.6 MB each way per frame, well under 1 ms on PCIe.
- ONNX Runtime/DirectML runs on its own D3D12 device.
- The depth result (518×294 floats) is uploaded back into a D3D11 texture.

This avoids D3D11 ↔ D3D12 shared-resource interop. It also makes hybrid laptops work naturally:
capture and presentation run on the GPU driving the glasses, inference on the high-performance
GPU (§4.3). Zero-copy IOBinding is an optional V8 optimization, only if V0/V3 show the copies
matter.

### D6. Presentation: one transparent overlay window with DirectComposition

The engine owns one Win32 window covering the whole destination display.

- **Extended styles:** `WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT |
  WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`.
- **Content:** a DComp visual whose content is a premultiplied-alpha composition swap chain,
  `DXGI_SWAP_CHAIN_FLIP_DISCARD`, with a frame-latency waitable object.
- **Pixels:** transparent everywhere except the two eye rectangles of the video.
- **One swap chain holds both eyes**, so left and right always flip together; they are never a
  frame apart.
- The window never changes size while the video moves or scrolls; only its content does.
- Gate G3 in V0 verifies click-through (required when source = destination) and cross-process
  stacking.

*Rejected:*

- **WPF `D3DImage`.** Extra copy and WPF composition latency, and it is in-process.
- **Two windows (one per eye).** Two presents that can land in different DWM frames.
- **Layered window with `UpdateLayeredWindow`.** CPU copies.

### D7. Main app: thumbs stacked as one chain

**Done** as part of the black-screen fix (2026-10-07): `restackIfOrderChanged` now sets the whole
overlay stack as one chain, with the backdrop at the bottom. What remains for this plan is to
insert the engine overlay just before the target window's thumbs.

Before that fix, `restackIfOrderChanged` built two interleaved chains: each left thumb behind the left thumb
in front of it, and each right thumb likewise. The global order ends up as
`cursorL, B_L, A_L, …, cursorR, B_R, A_R, …`. A single window spanning both eyes has no valid
place in that order.

New order: `cursorL, cursorR, B_L, B_R, A_L, A_R, …`. Each window's left thumb goes behind the
previous window's right thumb, and its right thumb behind its own left thumb. The engine overlay
is inserted just before the target window's thumbs.

Visually this is identical, because the two eyes never overlap. It is the same number of
`SetWindowPos` calls.

### D8. Finding the video: no extension

No browser extension (the user prefers none) and no UI Automation by default. In order of
precision:

1. **Fullscreen video** (§5.1): exact, and the main way video is watched.
2. **Picture-in-picture** (§5.2): Edge and Chrome can pop a video into its own window that holds
   nothing but the video. Exact, and leaves the page usable beside it.
3. **In-page video by motion** (§5.3): automatic, best effort; briefly flat while scrolling.
4. **Manual pick and lock** (§5.6).

*Rejected or deferred:*

- **Companion extension with native messaging.** Exact for in-page video, but the user does not
  want one. Revisit only if in-page detection proves annoying in V2.
- **UI Automation.** Chrome turns on full renderer accessibility when a UIA client appears,
  which costs page performance. V0 may measure it once; it is not in the plan.
- **Doing the conversion inside the browser** (WebGPU, like Universal 3D Viewer's unfinished AI
  mode). Several times slower than native TensorRT, which rules out the larger tiers.

---

## 4. The per-frame pipeline

### 4.1 Stages

| # | Stage | Where | Output |
|---|---|---|---|
| 1 | Frame arrives (WGC `FrameArrived`) | capture thread | BGRA texture, timestamp `t_cap` |
| 2 | Region update: downsample to ~1/8, luma, read back | GPU + CPU | change map → video rect (§5) |
| 3 | Crop + letterbox trim + resize + normalize to model input | GPU compute | small staging buffer |
| 4 | Read back, run the model | inference thread (ORT) | raw inverse depth `r` |
| 5 | Upload, normalize, temporal filter, upsample, dilate, static overlays | GPU compute | nearness `n` at eye resolution (§6) |
| 6 | Warp both eyes, fill holes, floating window | GPU compute | two eye images (§7) |
| 7 | Composite into the swap chain at the eye rectangles, present | render thread | on the glasses |

Stages 1–3 and 5–7 run on one D3D11 device (the adapter driving the destination). Stage 4 runs on
the inference adapter. Each stage keeps only the newest item, so a slow stage drops frames and
never queues them.

### 4.2 Budget

| Stage | Target |
|---|---|
| 1–3 capture, region, preprocess | ≤ 1.5 ms |
| 4 inference, Balanced preset | **≤ 12 ms** (V0 measures; this is the go/no-go number) |
| readback + upload | ≤ 1 ms |
| 5–6 post + warp (2 × 1920×1080) | ≤ 2 ms |
| 7 present | next vsync |

At 30 fps video there is 33 ms per frame; at 60 fps, 16.7 ms. Inference only runs when a new
video frame arrives, so a 24 fps film costs 24 inferences per second, whatever the display rate.

**Presets.** Model input, long side × short side, each a multiple of 14 (the ViT patch). The
short side follows the video's aspect ratio. The tier (D3) and the preset are chosen together;
Auto picks the strongest pair that fits.

| Preset | 16:9 input | Use |
|---|---|---|
| Fast | 392×224 | integrated GPUs, battery |
| Balanced | 518×294 | default |
| Quality | 756×420 | fast discrete GPUs |
| Auto | picks the largest preset whose rolling p95 inference time stays under 70% of the frame period | |

### 4.2a Speed techniques

In order of payoff:

1. **TensorRT fp16 through Windows ML** (D4), one static-shape engine per tier and preset,
   cached on disk.
2. **Infer only on new video frames.** WGC delivers a frame when the window changes, so a 24 fps
   film costs 24 inferences per second and a paused video none.
3. **Model resolution only.** Depth runs at the preset size and is brought to full resolution by
   the guided upsample (§6.3), which costs under 1 ms.
4. **Crop before inference.** Letterbox and pillarbox bars (§5.5) are cut off first.
5. **Hardware optical flow for 60 fps.** RTX 20 and later have an optical flow accelerator
   (NVIDIA Optical Flow SDK, D3D11/D3D12). For 60 fps video with a tier that cannot keep up,
   run the model on every other frame and carry depth to the frame in between along the
   measured motion. Almost free, and it lets Large run on 60 fps video.
6. **Pipelining.** Capture, preprocessing, inference and warp work on different frames at once,
   so added latency is about one frame plus inference, not the sum of the stages.
7. **Zero-copy on single-GPU machines.** When capture and inference share the adapter (§0),
   bind D3D12 resources to the session directly instead of the CPU round trip of D5.
   Promoted from "optional" for the Large tier, where every millisecond counts.

### 4.3 GPUs

- **Presentation/capture device:** the adapter whose output drives the destination display, so
  there is no cross-adapter copy of the final picture.
- **Inference device:** `DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE` by default, user-selectable in
  Settings. The DirectML EP's `device_id` is the DXGI adapter enumeration index.

On a hybrid laptop this runs the model on the discrete GPU while the iGPU drives the glasses, at
the cost of only the small tensor copies (D5).

On the development machine (§0) both devices resolve to the RTX 3060. One thing to check in V0:
Windows may run Edge and Chrome on the Iris Xe (power-saving GPU preference). Then DWM already
copies their frames across to the NVIDIA outputs, as it does today for the thumbnails. Measure
whether capture adds cost in that case. If it does, the fix is the per-app "High performance"
setting in Windows Settings › Display › Graphics, not code.

### 4.4 Latency and audio sync

Video added latency is about `t_infer + one vsync`. The browser's audio is not delayed, so the
picture trails the sound by that much. Audio leading video becomes noticeable at around 45 ms, so
the target is **≤ 40 ms added**.

Pairing policy:

- Frame N waits for its own depth up to a deadline (default 16 ms after `t_cap`).
- If the depth is late, frame N is shown with the newest depth available ("stale depth", counted
  in stats). The picture is never delayed further to wait for depth.
- Depth is low-frequency and temporally smoothed, so one frame of staleness is rarely visible
  except at fast motion edges.

---

## 5. Finding the video rectangle

### 5.1 Fullscreen

The target window covers its monitor (within 4 px, same tolerance as `isFullScreenOverlay`) and
has no caption. The video rectangle is the whole client area, then letterbox-trimmed (§5.5). This
covers the main "watch a video" case and needs no motion analysis.

### 5.2 Picture-in-picture

Edge and Chrome open a picture-in-picture video in a separate top-level window that contains
only the video and its overlay controls. The main app recognizes it: a window of the browser's
process with no caption, titled "Picture in picture" (localized; matched against a small list
plus the window's always-on-top, small, video-shaped bounds). The video rectangle is the whole
client area, letterbox-trimmed. The player controls that appear on hover are a static overlay
(§6.5) and sit at the screen plane.

### 5.3 In-page video (motion)

Per frame:

1. Downsample the window capture to a ~1/8 grid (240×135 for a 1080p window) of luma, on the
   GPU, and read it back (32 KB).
2. Per cell, accumulate change energy with decay: `E ← 0.9·E + |ΔY|`, a time constant of about
   2 s at 30 fps.
3. Threshold `E` into a mask, close small gaps (3×3 morphology), and take connected components.
4. Candidate = the bounding box of the largest component. It must pass:
   - area ≥ 4% of the window and ≥ 320×180 source px,
   - aspect between 1.0 and 2.5 (after letterbox trim),
   - fill ratio of the mask inside the box ≥ 0.6.
5. Refine each edge at full resolution: within ±12 px of the coarse edge, take the position of
   the strongest straight edge (column or row gradient summed along the edge). Video elements are
   axis-aligned rectangles with a sharp border.
6. Accept only after the rectangle is stable (±2 px) for 0.5 s.

### 5.4 Tracking

- **Window moved or resized** (from the Scene message): translate the rectangle, and hide the
  overlay until the window has been still for 150 ms.
- **Page scrolled:** the static page content moves while the rectangle's inside keeps changing.
  Detect a global vertical shift of the static cells (1-D correlation of row profiles), move the
  rectangle with it, and hide the overlay until it has been stable for 150 ms.
- **Lost** (no change inside for 3 s): keep the rectangle but stop inference. This is how a
  paused video looks. Re-detect when change resumes or the layout changes.
- **Target window changed:** fade out, re-detect.

### 5.5 Letterbox and pillarbox

Rows and columns at the rectangle's edges that stay near-black (luma < 16/255, variance < 2)
across 1 s are trimmed from depth statistics and rendered at the screen plane (nearness = c).
The same logic handles pillarboxed vertical video.

### 5.6 Manual control

- **Pick** (`Ctrl+Alt+Shift+D`): use the changing region under the cursor, and lock it.
- **Lock/unlock:** stops auto-detection from switching the rectangle; useful on pages with
  animated ads.
- The rectangle is drawn as a thin outline in both eyes for 1 s after any pick or lock, so the
  user sees what was chosen.

### 5.7 Pure, testable core

`RegionTracker` takes the grids plus window rectangle changes and returns rectangle and state. It
has no GPU and no Windows state. It is tested offline on recorded grid sequences (§11.1), in the
same spirit as `StereoGeometry`.

---

## 6. Depth post-processing

The model gives relative inverse depth `r` per frame. Its scale and offset change from frame to
frame, so raw output "breathes" and flickers. Everything here runs on the GPU at depth
resolution, except the upsample and dilation, which run at eye resolution.

### 6.1 Normalization

- Histogram of `r` inside the video rectangle, excluding letterbox and static-overlay pixels.
- Take the 2nd and 98th percentiles `p2, p98`.
- Smooth them over time with an exponential moving average, time constant `τ ≈ 0.5 s`, reset on a
  scene cut.
- Nearness: `n = clamp((r − p̄2) / (p̄98 − p̄2), 0, 1)`, 1 = nearest.
- Optional perceptual curve `n ← n^γ`, with γ = 1 by default; V5 tunes it.

### 6.2 Temporal filtering

- Per pixel: `n_t = lerp(n_{t−1}, n_raw, α)`, with `α = α_min + (1 − α_min) · clamp(k·|ΔY|)`.
- Where the picture changes, depth follows at once; where it is still, depth settles.
- `α_min ≈ 0.2` at 30 fps, scaled for frame rate.
- **Scene cut** (mean |ΔY| on the 1/8 grid above a threshold, or a histogram distance jump):
  reset all temporal state and the percentile averages.

### 6.3 Upsampling to eye resolution

Joint bilateral upsampling guided by the full-resolution luma:

- 5×5 taps on the depth grid;
- weights = spatial Gaussian × range Gaussian on |Y_hi(p) − Y_hi(q)|.

This puts depth edges on picture edges instead of blurring them across. A plain bilinear
upsample makes halos around people.

### 6.4 Foreground dilation

A max filter of `n` with a 2–4 px radius at eye resolution. It pushes the error band at object
borders onto the background, where hole filling (§7.3) hides it.

### 6.5 Static overlays: subtitles, logos, player controls

- Pixels whose colour has not changed for 1 s while the rest of the frame has changed (global
  change above threshold) are marked static. Their nearness is forced to the convergence value
  `c`, so they sit at the screen plane.
- This handles burned-in and HTML subtitles, channel logos and a lingering control bar. The model
  otherwise puts text at random depths and splits it across depth edges, which is the most common
  complaint about converted video.
- A paused video (no global change) does not trigger it.

---

## 7. Stereo synthesis

### 7.1 Disparity model

Sign conventions are those of math.md §5.4: crossed disparity is positive, the left copy moves
right, and positive means toward the viewer.

Symbols:

- `B` — comfort budget in destination framebuffer px: `1° × ppd_x`, `StereoGeometry.PixelsPerDegreeX`.
- `δw` — the browser window's per-eye shift: `ShiftForLevel(level)`, destination px.
- `n(x)` — nearness in [0, 1]; `c` — convergence (the nearness that sits on the window plane).
- `σ` — strength setting in [0, 1].

Per-eye shift of a video pixel, and the total disparity it produces:

```
s(x)  = δw + (S/2) · (n(x) − c)          left eye: +s(x), right eye: −s(x)
D(x)  = 2δw + S · (n(x) − c)             total disparity
```

The comfort cap applies to both extremes, crossed and uncrossed:

```
2δw + S·(1 − c) ≤ B       (nearest pixel)
S·c − 2δw       ≤ B       (farthest pixel)
S_max = min( (B − 2δw)/(1 − c),  (B + 2δw)/c )
S     = σ · S_max
```

GT Max, Full SbS, video fullscreen (δw = 0): B = 37 px (math.md §5.4 table).

| c | S_max | σ = 0.6 → S | Range of D |
|---|---|---|---|
| **0.35 (default)** | 56.9 | 34 px | −12 px (behind) … +22 px (in front) |
| 0.5 | 74 | 44 px | −22 … +22 |
| 0.2 | 46.3 | 28 px | −6 … +22 |

Defaults are σ = 0.6, c = 0.35: mostly behind the screen with moderate pop-out, which is the
comfortable side.

- **In-page video.** A window at level L is already in front by 2δw, which eats crossed budget.
  S shrinks automatically; the video never exceeds the budget.
- **Half SbS.** `ppd_x` is in framebuffer px, so B halves, as math.md §5.4 already requires.
- **Top-bottom.** Disparity stays horizontal; only the eye rectangles change.

These formulas go into math.md as §8 during V5, together with the invariants of §7.5.

### 7.2 Where the eyes are

The video rectangle `R` (source px) maps through the same `StereoGeometry` as the thumbs.
`StereoGeometry.cs` is compiled into the engine as a linked source file: it is pure and C# 7.3.

- `MapRect(R, eye, 0)` gives the eye rectangle; `Picture(eye)` clips it.
- Sharp 1:1, Keep ratio, pixel aspect and custom areas all come for free.

The engine receives the geometry's `Create` arguments and the viewport in each Scene message, and
builds an identical instance.

### 7.3 Warp (compute shaders, per eye)

1. **Resample.** Video and `n` to the eye rectangle's resolution; there is one sample per output
   pixel.
2. **Forward splat with a depth test.** Each sample goes to `x' = x ± s(x)`.
   - Write the 32-bit key `(n as 16 bit) << 16 | (x as 12.4 fixed point)` with `InterlockedMax`
     into a per-eye row buffer, at `floor(x')` and `ceil(x')`.
   - The nearest sample wins (occlusion), and splatting two pixels closes one-pixel cracks where
     depth stretches.
3. **Hole fill.** An empty target pixel is a disocclusion. Search up to `S` px left and right,
   take the neighbour with the **smaller** `n` (background) and use its colour. Disocclusions are
   always background revealed behind a foreground edge.
4. **Gather.** Colour = bilinear sample of the resampled video at the winning `x` (subpixel from
   the fixed-point part).
5. **Floating window.** Where a crossed-disparity object touches the left or right edge of the
   video, a stereo window violation occurs: the frame edge cuts an object that is in front of it.
   - Mask a dynamic black band, as wide as the largest crossed disparity found within 32 px of
     that edge.
   - The band goes on the left border of the left eye and the right border of the right eye.
   - It is smoothed over time (τ ≈ 0.3 s).

*Fallback* for weak GPUs: backward mapping by fixed-point iteration, `x_src ← x_out ∓ s(x_src)`,
4 iterations, in a single pass, without atomics. Quality is slightly lower at depth discontinuities.

### 7.4 Native 3D video

For video that is already SbS or TB 3D (V6), stages 3–6 are skipped:

- **Side-by-side:** the left half goes to the left eye and the right half to the right eye, each
  stretched to the full video rectangle; half-SbS content, the common case, is stretched ×2
  horizontally.
- **Top-bottom:** likewise, with the halves one above the other.

Detection runs on the 1/8 grid:

- **SbS:** the normalized cross-correlation of the left half against the right half, with the
  best horizontal offset within ±3%, is above 0.85 for 2 s.
- **TB:** the same, with the top half against the bottom half.
- There is hysteresis on both.

Settings offers Off / Auto / Force SbS / Force TB.

### 7.5 Invariants (offline harness, synthetic depth)

1. `n ≡ c` and `δw = 0` → both eyes are pixel-identical to the flat mapping, which is exactly what
   the thumb shows (this is also V1's passthrough exit criterion).
2. `n ≡ c`, `δw ≠ 0` → each eye equals the flat mapping shifted by ±δw (matches `WinSbS`).
3. For every pixel without occlusion, `x'_left − x'_right = D(x)` within 0.5 px.
4. `max |D| ≤ B` for every σ, c and δw.
5. There are no unwritten output pixels.
6. At an occlusion, the larger `n` is visible.
7. Swapping the eyes mirrors the shifts exactly: the warp is symmetric.
8. Letterbox rows and columns are unchanged, at `n = c`.

---

## 8. Integration with DesktopSbS

### 8.1 Protocol

**Transport.** A named pipe `\\.\pipe\DesktopSbS.Video.<session>.<pid>`, with its ACL restricted
to the current user. It uses length-prefixed binary messages with a protocol version.

`Model/VideoProtocol.cs` is shared by linking the main project's file into the engine, so it must
stay C# 7.3 and free of Windows and WPF types.

| Direction | Message | Content |
|---|---|---|
| main → engine | `Hello` | protocol version, settings |
| | `Settings` | enabled, σ, c, preset, inference adapter, native-3D mode, stats |
| | `Scene` | sequence number, target `HWND` (or none), window rect, window per-eye shift `δw`, geometry `Create` arguments + viewport, budget `B`, destination bounds |
| | `Suspend` / `Resume` | 3D paused, modal open, destination not in 3D, test pattern shown, excluded program maximized |
| | `Pick` / `Lock` / `Unlock` | manual region control (cursor position for Pick) |
| | `Shutdown` | |
| engine → main | `Ready` | overlay `HWND`, capture and inference adapter names, execution provider |
| | `Status` (≤ 4 Hz) | state (idle / searching / converting / native 3D / error), video rect, fps, inference ms p50/p95, added latency, stale-depth ratio |
| | `Error` | code, message |

A `Scene` is sent only when something in it changed. That happens per render pass at most, so it
is small and rare.

### 8.2 Main-app changes

| File | Change |
|---|---|
| `Model/VideoProtocol.cs` *(new, shared)* | messages and serialization |
| `Model/VideoTargetSelector.cs` *(new, pure)* | from the pass's window list: the front-most renderable window whose process is in `VideoApplications`, at least 25% of it visible after subtracting windows in front, not minimized. Unit-testable like `StereoGeometry` |
| `Model/VideoEngineClient.cs` *(new)* | starts the engine only when conversion is enabled; owns the pipe; restarts with backoff (1 s, 5 s, 30 s, then gives up and reports); stops it on exit |
| `View/MainVoidWindow.xaml.cs` | per pass: select the target and send the Scene; Suspend and Resume from `ApplyRenderState`; new commands in `ExecuteCommand` |
| `restackIfOrderChanged` + `WinSbS.StackBehind` | single-chain stacking (D7); insert the engine overlay before the target's thumbs |
| `Interop/Util.cs` | `RegisterExternalOverlay(hwnd)`, so `IsOverlay` excludes the engine window from enumeration (the "any new overlay" rule in CLAUDE.md) |
| `Options.cs` + Settings (3 files) | `VideoConversion`, `VideoApplications` (default `msedge.exe`, `chrome.exe`), `VideoDepth` (σ), `VideoPop` (c), `VideoQuality`, `VideoAdapter`, `VideoNative3D`, `VideoStats` |
| `Model/Enums.cs` | `ToggleVideo3D` (Ctrl+Alt+D), `IncreaseVideoDepth` / `DecreaseVideoDepth` (Ctrl+Alt+Shift+X / W), `PickVideo` (Ctrl+Alt+Shift+D). Existing saved shortcut lists gain the new defaults through the existing merge in `initKeyboardShortcuts` |
| `View/TrayIcon.cs` | "Convert video to 3D" check item and a video depth submenu; status in the tooltip |
| `View/SettingsWindow` | a "Video 3D" card: enable, app list, strength, pop-out, quality, GPU, native 3D, statistics, live status line |
| `View/AboutWindow` | new shortcuts in `displayOrder` |
| `View/TestPatternWindow` / engine | a "video depth" test: the engine renders a synthetic scene with known depth through the real warp (§11.4) |
| `DesktopSbS.Diagnostic` | GPU adapters, DirectX 12 feature level, whether the engine and model are present, execution provider |

The main app never touches D3D or the model. If the engine is missing (component not installed,
unsupported OS or GPU), the Video 3D UI is shown disabled with the reason.

### 8.3 Lifecycle rules

- The engine exits by itself when the parent process dies: it waits on the parent's process
  handle, so the overlay can never be orphaned.
- It hides its overlay whenever it is not sure (moving, scrolling, target lost, stale Scene
  > 200 ms). The flat thumb shows through.
- Suspend (pause, About or Settings open, 2D destination, test pattern) hides the overlay and
  stops capture. Resume restarts capture.
- Display change: the main app rebuilds as today and sends a new Scene. The engine recreates the
  swap chain for the new destination size.
- `DXGI_ERROR_DEVICE_REMOVED` or `RESET`: recreate the devices and the ORT session, and hide
  until ready.
- On battery: a setting chooses between "keep", "Fast preset" (default) and "pause conversion".

---

## 9. Engine project layout

```
DesktopSbS.Video/                       SDK-style, net10.0-windows10.0.22621.0, x64
  Program.cs                            args (pipe name, parent pid), parent watchdog, PMv2 manifest
  Ipc/PipeServer.cs
  Gpu/Devices.cs                        adapter choice, D3D11 device, device-lost handling
  Capture/WindowCapture.cs              WGC session, frame pool, borderless access
  Detect/RegionTracker.cs               pure (§5.7)
  Detect/RegionDetector.cs              GPU downsample + readback, feeds the tracker
  Depth/IDepthEstimator.cs
  Depth/OnnxDmlEstimator.cs             ONNX Runtime + DirectML
  Depth/WinMLEstimator.cs               Windows ML (TensorRT for RTX)
  Depth/FlowPropagation.cs              NVIDIA optical flow, depth for in-between frames (§4.2a)
  Depth/Presets.cs
  Post/DepthPost.cs                     §6
  Stereo/Warp.cs                        §7
  Present/OverlayWindow.cs              Win32 window, DComp, swap chain
  Shaders/*.hlsl                        compiled to bytecode at build (fxc), embedded
  Offline/OfflineRunner.cs              --offline: PNG frames in, SbS PNGs + timing CSV out
  models/*.onnx                         Distill-Any-Depth Base/Large, DA V2 Small (SHA-256 pinned)
  (linked) ../DesktopSbS/Model/StereoGeometry.cs, VideoProtocol.cs
```

**Packages:**

- `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.DirectComposition` (MIT, 3.8.x);
- `Microsoft.ML.OnnxRuntime.DirectML` (MIT; brings `DirectML.dll`).

Shaders are precompiled, so there is no runtime `d3dcompiler` dependency.

**Build.** The solution gains the project. MSBuild 18 builds SDK-style projects with the
.NET 10 SDK installed. The engine is published self-contained to `bin\<Config>\Video\`:

```
dotnet publish DesktopSbS.Video -c Release -r win-x64 --self-contained -o DesktopSbS\bin\Release\Video
```

**Requirements.** Windows 10 2004+ x64 (Windows 11 recommended) and a DirectX 12 GPU. The core
app keeps its own requirement of 1703+.

---

## 10. Phases

Sizes are relative: S = a day or two, M = a few days, L = a week or more.

### V0 — Feasibility spike (M) — throwaway, nothing in the repo

Console and WinForms harnesses in the scratchpad, run by me on this machine. Each gate is
go/no-go.

- **G1 Capture.** WGC of an Edge and a Chrome window playing YouTube with hardware decoding on:
  - frames are not black, and arrive at the video frame rate;
  - borderless capture works from an unpackaged exe;
  - capture keeps working while the window is partly covered.
  - *Fail →* D2 fallbacks.
- **G2 Inference.** Depth Anything V2 Small, Distill-Any-Depth Base and Large, fp16 where the
  export allows:
  - **ONNX Runtime + DirectML** on the RTX 3060 (and Small on the Iris Xe): p50/p95 over warm
    runs at 392×224, 518×294 and 756×420;
  - **TensorRT for RTX** on the RTX 3060: the same, plus first-run engine build time and cached
    start-up time;
  - GPU memory per session, against the ≤ 500 MB budget (Large may need more; record it);
  - a visual check of the depth maps on a few video frames, to confirm Large is worth its cost;
  - try Video Depth Anything Small streaming if an ONNX export is feasible.
  - *Decides* the default tier and preset per frame rate. *Fail* only if Base at 518×294 is over
    16 ms with both runtimes.
- **G3 Overlay.** A DComp window with the D6 styles:
  - transparent and click-through (clicks reach the browser underneath);
  - stackable by another process with `SetWindowPos` between two topmost windows;
  - no activation;
  - DWM cost of a full-screen transparent layer measured.
- **G4 Round trip.** Readback and upload of the model tensors, measured.

*Exit:* the numbers go into progress.md under Milestone 4; the default preset and the GPU policy
are decided.

### V1 — Engine skeleton and flat passthrough (L)

- The engine project, IPC, lifecycle and restart; single-chain stacking in the main app; overlay
  registration.
- The engine captures the target and renders the **video area flat** (n ≡ c) into both eyes.
- For V1 the rectangle is the whole client area of a fullscreen window, or a manual pick.

*Exit:*

- Screenshot diff between the overlay and the thumb underneath is ≤ 1 px, in Full SbS, Half SbS,
  TB, Keep ratio, Sharp 1:1, and source = destination.
- Killing the engine leaves the desktop rendering intact and it restarts.
- Single-chain stacking causes no visual change with conversion off (existing screenshots
  compared).

### V2 — Video detection and target selection (M)

§5 in full; `VideoTargetSelector`; recorded grid sequences for the offline tests.

*Exit:*

- On the §11.3 page set, the rectangle is correct within ±2 px on ≥ 95% of frames after the first
  second.
- No detection on video-free pages over 5 minutes, except animated ads large enough to pass the
  rules. Those are listed, and Lock covers them.

### V3 — Depth inference (M)

`IDepthEstimator` with both implementations (`WinMLEstimator`, `OnnxDmlEstimator`) and the
start-up choice between them; the TensorRT engine cache; presets and Auto; adapter selection; the
pairing policy (§4.4); stats.

*Exit:*

- Offline depth dumps look right on the clip set.
- The budget from V0 holds with the whole pipeline running.
- Passthrough never drops a frame because of inference.

### V4 — Depth post-processing (M)

§6 in full.

*Exit:*

- The flicker metric (mean |Δn| on still-camera clips) drops ≥ 70% against raw depth.
- Subtitles and logos sit at `n = c`.
- A scene cut settles within 2 frames.

### V5 — Stereo synthesis (L)

§7.1–7.3; math.md §8.

*Exit:*

- The §7.5 invariant harness passes on synthetic depth.
- Live review in the GT Max on the clip set: no double images, halos acceptable at σ = 0.6, no
  window violations at the edges.
- Defaults for σ, c and γ are confirmed by the user.

### V6 — Native 3D video (S)

§7.4.

*Exit:* SbS and TB YouTube 3D clips are auto-detected within 2 s and shown as native 3D; 2D clips
never trigger it.

### V7 — UI and settings (M)

§8.2 UI rows; README section.

*Exit:*

- Screenshots of the Settings card and tray in light and dark.
- Shortcuts work (including the Shift variants).
- Settings survive a restart and a downgrade (old settings untouched).

### V8 — Robustness and performance (M)

- Device lost (simulated with `dxcap -forcetdr` or a driver restart), display hot-plug, sleep and
  resume, battery policy, parent death.
- 30-minute soak with GPU memory and handle counts flat.
- Windows ML provider updates (Windows replaces TensorRT for RTX in the background):
  invalidate the engine cache when the provider version changes.
- Optional: zero-copy IOBinding if V3 showed the copies matter. On a single-GPU setup like §0,
  it is the only copy left.

*Exit:* the soak passes; every failure path ends in the flat thumb, never a stuck overlay.

### V9 — Packaging and docs (S)

- **Installer:**
  - an optional Inno Setup component "Video 3D conversion", files in `{app}\Video\`;
  - only offered on x64 Windows 10 2004+;
  - roughly 150–200 MB installed (runtime, DirectML, model), measured at build.
- **Licences:** `THIRD-PARTY-NOTICES.md` covering ONNX Runtime (MIT), DirectML (Microsoft
  redistributable licence), Vortice (MIT), the .NET runtime (MIT) and Depth Anything V2 Small
  (Apache-2.0, with its NOTICE).
- **Docs:** CLAUDE.md (engine architecture, the new overlay rule, the single-chain stacking,
  build), progress.md, math.md §8, and README (feature, requirements, DRM limitation, GPU
  requirement).

---

## 11. Testing

### 11.1 Offline mode (no screen takeover)

`DesktopSbS.Video.exe --offline <frames dir> --out <dir> [--depth synthetic|model] [--preset …]`

- Reads PNG frames; runs the whole pipeline except capture and presentation.
- Writes left, right, SbS and anaglyph PNGs, plus a per-stage timing CSV.
- It never opens a window, so it is safe to run from harnesses without asking. The live app and
  engine still need the user's go-ahead, per CLAUDE.md.

### 11.2 Harnesses (throwaway, as today)

- `RegionTracker` on recorded grid sequences.
- `VideoTargetSelector` on synthetic window lists.
- Warp invariants §7.5 via offline mode with synthetic depth.
- Protocol round trip (serialize and parse, version mismatch).

### 11.3 Clip and page set (live)

**Clips:** YouTube videos covering

- a talking head;
- a slow landscape pan;
- fast sport;
- 2D animation;
- a film with burned-in subtitles;
- a video with HTML captions on;
- a vertical short;
- a 4:3 pillarboxed clip;
- a SbS 3D clip and a TB 3D clip.

**Pages:** Vimeo, a Twitch stream, and a news page with an autoplaying muted video plus animated
ads.

**Views:** each in windowed, theater and fullscreen, in Edge and Chrome.

### 11.4 In-glasses checks

- The synthetic depth scene (step pyramid with known disparities) through the real warp: checks
  alignment, sign and comfort cap on the actual glasses.
- It is reachable from the test pattern.

### 11.5 Performance matrix

Every GPU in the user's machines × Fast, Balanced and Quality × 1080p and 4K source:

- inference p50/p95;
- added latency;
- stale-depth ratio;
- GPU memory;
- package power where readable.

Recorded in progress.md.

---

## 12. Packaging and licensing summary

| Item | Licence | Shipped |
|---|---|---|
| DesktopSbS.Video | GPLv3 (as the app) | yes |
| .NET 10 runtime (self-contained) | MIT | yes |
| ONNX Runtime, DirectML EP | MIT | yes |
| DirectML.dll | Microsoft DirectML redistributable licence | yes |
| Vortice.Windows | MIT | yes |
| Distill-Any-Depth Base and Large | MIT | yes (ONNX, pinned hashes) |
| Depth Anything V2 Small | Apache-2.0 | yes (fp16 ONNX, pinned hash) |
| Video Depth Anything Small | Apache-2.0 | only if V0 adopts it |
| Depth Anything V2 Base/Large/Giant, Video Depth Anything Large | CC-BY-NC-4.0 | **never**; user-supplied file only |

---

## 13. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Inference too slow on integrated GPUs | high (low on the RTX 3060) | Fast preset, Auto preset, battery policy; UI states the GPU requirement; flat fallback always works |
| Windows ML unusable from an unpackaged exe | low–medium | Gate G2; DirectML path works regardless |
| 6 GB VRAM contention (browser, DWM, games) | low | ≤ 500 MB budget; fp16; free the session while idle > 60 s |
| Temporal flicker / depth "breathing" | high | §6.1–6.2; Video Depth Anything streaming as an upgrade path |
| Halos and tearing at object edges | medium | guided upsampling, dilation, background hole fill, conservative σ default |
| Region detection fooled by animated ads | medium | size, aspect and stability rules; Pick/Lock; outline feedback |
| WGC yellow border on unpackaged exe | medium | Gate G1; sparse package; Desktop Duplication fallback |
| Overlay not click-through with DComp | low–medium | Gate G3 before any real code; per-eye windows as fallback (loses atomic flip) |
| Single-chain restack regresses stacking | low | V1 exit compares existing screenshots with conversion off |
| DirectML maintenance mode | certain (known) | `IDepthEstimator`; Windows ML estimator from V3 |
| Two runtimes (.NET 4.8 + .NET 10) | certain | Isolated in the engine folder; self-contained; documented in CLAUDE.md |
| Eye strain | medium | FOV-based cap on both crossed and uncrossed; defaults mostly behind screen; one-key toggle |
| Package size | certain | Optional installer component |
| Audio ahead of picture | medium | ≤ 40 ms target; never wait for depth past the deadline |

---

## 14. Defaults chosen, and later possibilities

Each default can be overridden before V1 starts:

1. Out-of-process .NET 10 engine (D1).
2. Self-contained, optional installer component, rather than requiring a system .NET 10 install.
3. Distill-Any-Depth Large for 24/30 fps and Base for 60 fps (pending V0), Depth Anything V2
   Small as the fast tier, plus our own temporal filter; non-commercial models only as a
   user-supplied file.
4. One converted video at a time, the largest visible.
5. Default apps: Edge and Chrome. Others are added in Settings.
6. Shortcuts: Ctrl+Alt+D toggle; Ctrl+Alt+Shift+X/W video depth; Ctrl+Alt+Shift+D pick.
   Shift variants avoid the AltGr clash.
7. Strength σ = 0.6, convergence c = 0.35, Balanced (or Auto) preset.

**Later, not planned:**

- A browser extension that reports exact `<video>` rectangles over native messaging (only if
  in-page detection proves annoying).
- Several simultaneous videos.
- Converting games or whole windows.
- Motion-compensated depth for the stale-depth case.

---

## Sources

- Depth Anything V2 (licences per model size): https://github.com/DepthAnything/Depth-Anything-V2
- ONNX export (fp16, dynamic shape, Apache-2.0 Small): https://huggingface.co/onnx-community/depth-anything-v2-small
- Video Depth Anything Small (Apache-2.0, 32-frame windows, streaming mode): https://huggingface.co/daydreamlive/Video-Depth-Anything-Small/blob/main/README.md
- FlashDepth (real-time streaming video depth, for context): https://arxiv.org/pdf/2504.07093
- DirectML maintenance mode and Windows ML: https://learn.microsoft.com/windows/ai/directml , https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html
- WGC borderless capture: https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired , https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscaptureaccess.requestaccessasync?view=winrt-20348
- TensorRT for RTX execution provider (Ampere / RTX 30xx and later, default in Windows ML): https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider , https://www.edge-ai-vision.com/2025/10/deploy-high-performance-ai-models-in-windows-applications-on-nvidia-rtx-ai-pcs/
- Distill-Any-Depth (MIT; Base 97.5 M, Large 300 M; ONNX exports): https://replicate.com/fater-ai/distill-any-depth-large/readme , https://huggingface.co/Ko033/distill-any-depth-base-onnx , https://huggingface.co/FuryTMP/Distill-Any-Depth-Large-onnx
- Video Depth Anything (streaming mode, model sizes, licences): https://github.com/DepthAnything/Video-Depth-Anything
- Universal 3D Viewer (reviewed 2026-10-07: WebGL display of existing 3D formats, unfinished AI depth mode): https://chromewebstore.google.com/detail/nhilpkoafpbiedgfhacdjflbhdnepinj
- Vortice.Windows (net9/net10): https://www.nuget.org/packages/Vortice.Direct3D11/ , https://packages.nuget.org/packages/Vortice.DirectComposition/
