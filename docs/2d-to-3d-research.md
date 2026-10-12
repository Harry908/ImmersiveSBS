# How other 2D-to-3D converters work, and what to take from them

Research report, 2026-10-10. Written after the first glasses run of the fp16 build showed
shadowing around people, jagged and tearing edges on doors, and large models that only worked
while the video was paused (`progress.md`). The question: how do the best-known open-source
converters produce their stereo, and how do they make it fast? Sources are the projects' own
code and documentation, read on 2026-10-10. Statements marked *(inference)* are mine, not the
projects'.

## 1. Summary

1. **They prefer soft depth edges and stretched pixels to sharp edges and filled holes.** iw3's
   default "Edge Fix" blurs and grows the near side of every depth edge at model resolution, and
   its author calls the stronger setting "the most eye-friendly". Depth3D's default occlusion
   mode is "Alpha", a stretch. ImmersiveSBS does the opposite: a hard dilation in screen
   pixels, then edge snapping, which makes every depth edge a step, then a splat that leaves
   holes, then a fill. That chain is the most likely source of the shadowing and the jagged
   door edges.
2. **Depth Anything V2 has stair-stepped depth edges.** iw3's author found that Depth Anything V2
   and Video Depth Anything produce jagged depth edges that turn into jagged warping, and that
   Distill-Any-Depth's output is already smooth. DA3 shares DA2's architecture and probably
   behaves the same *(inference)*. Our edge snapping sharpens exactly those steps.
3. **Depth always matches its picture.** iw3's real-time desktop mode computes depth for a
   frame and shows that same frame: the picture waits for its depth, and edges never tear. We
   show the newest picture with whatever depth is ready. With a large model that depth is
   100–170 ms old, and moving edges tear.
4. **Real time is hard for everyone.** iw3's desktop mode defaults to the smallest model, mild
   depth and a 15 fps stream. Depth3D is real time only because a game hands it an exact depth
   buffer; it runs no model.
5. **The fast ones never copy frames to the CPU.** iw3 captures into CUDA memory and displays
   from it through CUDA/OpenGL interop. Our model input still makes a GPU → CPU → GPU trip.

## 2. Projects surveyed

| Project | Stars | Kind | Why it matters here |
|---|---|---|---|
| [nagadomi/nunif](https://github.com/nagadomi/nunif) — iw3 | ~3,500 | PyTorch converter for files, plus `iw3-desktop`, a real-time desktop mode | The most used open-source converter; same models as ours (DA2, DA3, Distill-Any-Depth, Video Depth Anything); updated daily |
| [BlueSkyDefender/Depth3D](https://github.com/BlueSkyDefender/Depth3D) — SuperDepth3D.fx | ~760 | ReShade shader using a game's depth buffer | Years of work on artifact-free warping at game frame rates, all in shaders |
| [DepthAnything/Video-Depth-Anything](https://github.com/DepthAnything/Video-Depth-Anything) | ~2,200 | Temporally consistent depth model | Has an experimental one-frame-at-a-time streaming mode |
| [TencentARC/StereoCrafter](https://github.com/TencentARC/StereoCrafter) | ~530 | Offline: depth splat, then video diffusion to fill holes | The quality ceiling; far too slow for live use |
| [VisionDepth/VisionDepth3D](https://github.com/VisionDepth/VisionDepth3D) | ~340 | Offline converter app | Same family as iw3's offline mode; not read in depth |

## 3. How they make the stereo

### 3.1 Depth preparation (iw3)

Each step below runs on the depth map at model resolution, before any upscaling.

- **Input:** resized so each side is a multiple of 14 (the ViT patch size), with an
  anti-aliased bilinear downscale.
- **Precision:** fp16 autocast. Optionally, test-time augmentation: run the frame and its mirror
  image, then average the two.
- **Depth anti-aliasing** (`--depth-aa`): a small learned network with three window-attention
  blocks that smooths the stair-stepped edges of DA2 and VDA
  ([issue #406](https://github.com/nagadomi/nunif/issues/406)). The author notes the difference
  "isn't noticeable when used together with Edge Fix".
- **Edge Fix** (`--edge-dilation`, default 2; `dilation.py`). Per iteration:
  1. Edge weight: the local 3×3 depth range, standardised and scaled to 0..1.
  2. Blur the depth with a 3×3 Gaussian, then take a 3×3 max. On disparity, the max grows the
     near side.
  3. Blend: `depth = depth·(1−w) + dilated·w`.

  Only edges change, and they come out soft. Horizontal and vertical strength can be set
  separately.
- **Normalisation:** min–max with an exponential moving average (`--ema-decay`, 0.75–0.99) and
  an optional look-ahead of up to 150 frames (`--ema-buffer`). A scene-cut detector
  (TransNetV2) resets the state. Ours is the same idea, using percentiles.
- **Depth-to-disparity curves** (`mapper.py`). Relative-depth models get softplus curves that
  push the foreground forward ("foreground scale"). Metric models get distance → disparity
  curves. DA3 outputs depth, so it needs one of these; we use 1/d.

### 3.2 The warp

iw3 has several methods (`backward_warp.py`, `stereo_model_factory.py`):

- **`backward`:** plain `grid_sample`, a lookup in the source image with the source depth. It
  never leaves holes, and pixels stretch across depth edges. It is inexact at edges, but it
  never tears.
- **`row_flow_v3`** (the default): a small network, two window-attention blocks at 1×8
  downscale and 64 channels, reads depth, the stereo strength and the convergence, and outputs
  a horizontal offset per pixel; then `grid_sample`. It is trained to look like real stereo
  within depth strength 0–5 and convergence 0–1.
- **`mlbw_l2` / `mlbw_l4`:** multi-layer backward warp. The network outputs 2 or 4 offsets
  and blend weights per pixel, and each layer is warped and weighted. This handles the
  overlap at depth edges better than a single offset; a variant also predicts a hole mask.
- **`forward_fill`, `forward_inpaint`:** forward splat, then a simple fill or a learned
  inpainting network. StereoCrafter does this with video diffusion.
- **Both eyes** (`--synthetic-view both`, the default): each eye gets half the shift, so each
  carries half the artifacts. We already do this.
- **Screen border:** `preserve_screen_border` fades disparity to zero at the frame's sides. We
  have the same thing as the floating window.
- **Convergence** (`convergence_estimator.py`): a salient-object model at 192×192 finds the
  subject; convergence sits within the subject's 10–90 % depth range, smoothed over time.

Depth3D (`SuperDepth3D.fx`) uses **steep parallax mapping**. For each output pixel it marches
along the row through depth layers until it meets the surface, the technique games use for
relief textures. It is a lookup, like `backward`, but occlusion-aware: a near object correctly
hides what is behind it, and nothing is left empty. The "View Modes" decide what the gaps
beside objects show: stretch ("Alpha", the default), stamping, dithered reiteration, or a
frosted filtered version. "Infill Blur" softens the stretched areas. "Memory Infill" fills
gaps with background remembered from earlier frames, using motion vectors. "Halo Priority"
masks the halo reduction to foreground objects. Its depth anti-aliasing smooths depth along
each edge's direction, found with a Sobel filter, and never across the edge.

## 4. How they make it fast

### 4.1 iw3-desktop (`iw3/desktop/`)

- **Capture straight into GPU memory.** `screenshot_thread_cuda.py` uses `wc_cuda` (by the
  same author): Windows Graphics Capture frames arrive as CUDA tensors. The swizzle, crop and
  resize happen on the GPU; no frame goes through the CPU.
- **One synchronous loop:** grab the newest frame → depth → stereo for that frame → output →
  sleep to the frame cap (`--stream-fps`). Depth and picture always match. Throughput is
  whatever the whole chain manages: depth is not overlapped with presenting.
- **Model execution:** PyTorch with fp16 autocast and optional `torch.compile`. Batch size 1 in
  desktop mode; the README warns this is "considerably lower" fps than file conversion.
- **Output on the GPU too:** the local viewer uses CUDA/OpenGL interop
  (`cudaGraphicsGLRegisterBuffer`). The web stream can encode JPEG on the GPU.
- **Defaults:** `Any_V2_S`, divergence 1, convergence 1 (everything behind the screen), 1080p,
  15 fps stream (30 maximum through the browser).

### 4.2 Depth3D

- **The march's cost scales with disparity:** the step count is the shift in pixels × a
  quality factor of 0.625, 0.75 or 1.0 ("Performant", "Normal", "High"; "Performant" is the
  default).
- **Distance-field skip:** big steps (2×) where the depth is flat, fine steps (1.25×) at
  edges.
- **"Nearest depth chain":** the minimum depth over 16-pixel runs, so the march can jump an
  empty span in one step. This is the hierarchical trick from relief mapping.
- **Depth at reduced resolution** (`Depth_Rez`) with mip chains. Masks read coarse mips (2–3);
  a blurred depth buffer at a quarter resolution drives the masks.
- **Precompute once a frame:** an image-structure field at half resolution, read with one fetch
  per gap pixel.
- **ReShade's "Performance Mode"** compiles the settings in as constants.

### 4.3 Video Depth Anything streaming

The streaming mode caches the hidden states of the temporal attention and sends one frame per
call. The authors report a quality drop without fine-tuning: ScanNet d1 falls from 0.926 to
0.836. Small is Apache-2.0; Base and Large are CC-BY-NC-4.0. iw3 offers it as `VDA_Stream_*`
and normalises with a moving average instead of the original global min/max.

## 5. Our method against theirs

### 5.1 The ImmersiveSBS pipeline (2026-10-10)

1. **Find the video.** `RegionTracker` finds the playing video on an 8×8 luma grid of the
   virtual monitor. With the browser extension, the page's exact video box replaces the
   tracker's while the two agree. `Native3DDetector` passes video that is already 3D straight
   through.
2. **Capture.** DXGI Desktop Duplication into a D3D11 texture. The video region is cropped on
   the GPU.
3. **Model input.** The `Preprocess` compute shader downscales the region to the model's size
   (4 bilinear taps per pixel) and normalises it. The result is read back to the CPU after the
   frame is presented.
4. **Depth model.** `DepthService` runs ONNX Runtime with TensorRT for RTX on its own thread.
   Engines are fp16 and compiled once per GPU. Models: Depth Anything V2 S/B/L, Distill-Any-Depth
   B/L, Depth Anything 3 S/B.
5. **Post-processing, on the CPU at model resolution** (`DepthPostProcessor`):
   - DA3's depth inverted to nearness.
   - 2nd–98th percentile normalisation, smoothed over 0.5 s.
   - Per-pixel temporal smoothing that follows motion.
   - Luma-based scene cuts.
   - Static overlays (logos) and subtitles (`CaptionLock`) held flat.
6. **Eyes, on the GPU at full resolution** (`StereoSynth`):
   1. `Upsample`: colour-guided (joint bilateral) upscale of the depth to the region's size.
   2. `Snap`: moves depth edges onto the picture's edges.
   3. `Dilate`: grows the near side by `DilateRadius` screen pixels (hard maximum).
   4. `Splat`: forward warp. Each source pixel is written to its shifted place in each eye,
      and the nearest wins (`InterlockedMax`).
   5. `Resolve`: fills holes by mirroring the background beside them, with a 3×3 tent blur.
   Both eyes are shifted by half each. The floating window fades disparity at the side edges.
7. **Timing.**
   - **Live:** the newest picture with the newest depth. A stale guard shows the video flat
     when depth falls more than 250 ms behind.
   - **Buffer:** the picture is shown 0.5–2 s late, each frame with its own depth averaged over
     its neighbours, with the sound delayed to match (in the page with the extension, by
     process loopback otherwise).
8. **Present.** A flip-model swap chain on the glasses, side by side.

### 5.2 Stage by stage

"To do" refers to the recommendations in §7.

| Stage | iw3 / Depth3D | ImmersiveSBS | To do |
|---|---|---|---|
| **Finding the video** | None: the whole screen or a chosen window; iw3 crops black bars | Region tracker, plus the extension's exact box; native-3D pass-through | Nothing; we are ahead |
| **Capture** | Windows Graphics Capture straight into CUDA memory (iw3); the game's depth buffer (Depth3D) | Desktop Duplication into D3D11; crop on the GPU | Nothing |
| **Model input** | Anti-aliased resize to multiples of 14, kept on the GPU | Anti-aliased 4-tap downscale on the GPU, then **read back to the CPU** | Keep it on the GPU (§7 item 5) |
| **Depth model** | PyTorch fp16, optional `torch.compile`, optional mirror averaging | TensorRT for RTX fp16 engines, cached per GPU; faster per frame | Nothing; we are ahead |
| **Depth to disparity** | Curves per model type (softplus "foreground scale", distance → disparity for metric models) | Linear; 1/d for DA3; strength from a comfort budget | Optional foreground-scale curve (later) |
| **Normalisation and temporal** | EMA min–max with up to 150 frames of look-ahead; a learned scene-cut detector; streaming Video Depth Anything | EMA percentiles; per-pixel smoothing that follows motion; luma scene cuts; neighbour averaging in the buffer | Video Depth Anything Small streaming (§7 item 6) |
| **Edge treatment** | **Soft**: blur + max blended at depth edges, at model resolution (iw3, default 2); stretch and infill blur (Depth3D) | **Hard**: `Snap` sharpens edges, then a screen-pixel max filter | Soft edges at model resolution; snapping off by default (§7 item 2) |
| **Stair-stepped edges** (DA2, DA3) | Learned depth anti-aliasing (iw3); smoothing along each edge's direction (Depth3D) | None | Depth anti-aliasing (§7 item 3) |
| **Depth upscaling** | Bilinear, inside the warp's lookup | Colour-guided upsample at full resolution | Keep; it helps once edges are soft |
| **Warp** | **Lookup**: `grid_sample`, a learned per-pixel offset, or multi-layer blends (iw3); occlusion-aware row march (Depth3D) | **Forward splat**, nearest wins | Row-march lookup as a new mode (§7 item 4) |
| **Gaps beside objects** | None left (lookup stretches); infill blur; background remembered from earlier frames (Depth3D) | Mirrored background + tent blur | Goes away with the lookup warp; remembered background later |
| **Depth vs. picture timing** | Always the same frame (iw3 desktop: the picture waits) | Live: newest picture with older depth; exact only with the buffer | Matched mode (§7 item 1) |
| **Convergence** | Salient-subject estimate (iw3, optional) | Fixed screen plane, set by the user | Later |
| **Screen edges** | Disparity faded to zero at the sides | Floating window | Nothing |
| **Text, subtitles, logos** | Nothing specific; iw3 warns that GUI text and windows may get wrong depth | Subtitle lock; static overlays held flat | Nothing; we are ahead |
| **Sound** | iw3 desktop plays the PC's sound live, so it runs ahead of the processed picture *(inference)* | Delayed sound with the buffer; in-page delay with the extension | Nothing |
| **Data path** | No CPU copies from capture to display (iw3) | D3D11 throughout, except the model's input and post-processing on the CPU | Keep frames on the GPU (§7 item 5) |

### 5.3 Where that leaves us

**Ahead:**
- Model speed: TensorRT fp16.
- Live features: finding the video, subtitle lock, static overlays, delayed sound, the theater.

**Behind, and the cause of what the glasses showed:**
- Edges: hard instead of soft, no anti-aliasing.
- The warp: a splat with holes instead of a lookup.
- Timing: depth not matched to its picture in live mode.

These three are §7 items 1–4, in that order.

## 6. What the glasses run showed, explained

- **Shadowing around people:** hard dilation (the saved tuning has radius 2) plus snapping
  makes the near side of each edge a step. The splat then opens a hole beside it, and the
  fill paints there.
- **Jagged, pixelated door edges:** DA2 and probably DA3 output stair-stepped depth edges
  *(the DA3 part is inference)*. Snapping turns those steps into hard steps in the warp, which
  is visible on long straight edges such as doors.
- **Tearing on moving edges:** a large model's depth is 100–170 ms old in live mode (measured
  in the run's log), so it no longer lines up with a moving edge.
- **Large models only when paused:** a guard bug, fixed (`AiStereo.DepthFreshMs`). It
  explains the flat picture, not the tearing.

## 7. Recommendations

In order of value for effort.

1. **Matched mode for slow models.** Delay the picture by the depth's measured latency
   (50–150 ms) so every shown frame has its own depth. `VideoDelayBuffer` already does this
   for the buffer; this would be a short automatic delay instead of 0.5–2 s. With the
   extension, delay the sound by the same amount. Without it, the sound leads by that much:
   about 50 ms is at the edge of what people notice, 150 ms is noticeable. Small; it removes
   the tearing.
2. **iw3-style soft edges at model resolution, replacing the screen-pixel dilation.** The
   algorithm in §3.1: edge weight, blur, max, blend, N passes, on the CPU nearness map in
   `DepthPostProcessor`. Cheap at 518×294. Default 2, with a setting. Edge snapping off by
   default, or only for Distill-Any-Depth. Small; targets the shadowing.
3. **Depth anti-aliasing for DA2 and DA3.** Depth3D's approach: find each edge's direction
   with Sobel and blur along it, at model resolution. No network needed. Small to medium;
   targets the jagged doors. Check first whether recommendation 2 alone is enough: iw3's
   author found the soft dilation hides most of it.
4. **A lookup-based warp, Depth3D-style, as a new warp mode.** Per output pixel, march along
   the row through the nearness map with the distance-field skip and a coarse min-depth chain,
   then stretch into gaps with an optional infill blur. It removes holes, and with them most
   of the hole-fill artifacts. Medium to large; best judged by an A/B against the splat in the
   tuning panel.
5. **Keep frames on the GPU.** Register the D3D11 model-input buffer with CUDA
   (`cudaGraphicsD3D11RegisterResource`, as `wc_cuda` does for capture) and hand it to
   TensorRT through ONNX Runtime IOBinding. That removes the model input's trip through the
   CPU (up to 3.8 MB each way per depth frame) and its waits. The CPU post-processing would then need to move to compute shaders
   to keep the gain. Large; worth it once depth latency is the bottleneck.
6. **Later:**
   - Video Depth Anything Small in streaming mode, for steadier depth (Apache-2.0; expect some
     quality loss).
   - Salient-subject convergence.
   - Remembered-background infill (Depth3D's "Memory Infill").

## 8. Sources

- nunif / iw3: [repository](https://github.com/nagadomi/nunif),
  [iw3 README](https://github.com/nagadomi/nunif/blob/master/iw3/README.md),
  [desktop mode](https://github.com/nagadomi/nunif/blob/master/iw3/docs/desktop.md),
  [depth anti-aliasing, issue #406](https://github.com/nagadomi/nunif/issues/406),
  [edge dilation notes, issue #97](https://github.com/nagadomi/nunif/issues/97#issuecomment-2027349722).
  Code read: `iw3/dilation.py`, `backward_warp.py`, `mapper.py`, `depth_scaler.py`,
  `convergence_estimator.py`, `stereo_model_factory.py`, `depth_anything_model.py`, `utils.py`,
  `models/row_flow_v3.py`, `models/depth_aa.py`, `desktop/utils.py`,
  `desktop/screenshot_thread_cuda.py`, `desktop/local_viewer.py`.
- Depth3D: [repository](https://github.com/BlueSkyDefender/Depth3D), `Shaders/SuperDepth3D.fx`
  (the `Parallax` march, performance levels, view modes, depth AA).
- Video Depth Anything: [repository and README](https://github.com/DepthAnything/Video-Depth-Anything)
  (streaming mode, licences).
- StereoCrafter: [repository](https://github.com/TencentARC/StereoCrafter),
  [paper](https://arxiv.org/abs/2409.07447).
- VisionDepth3D: [repository](https://github.com/VisionDepth/VisionDepth3D).
