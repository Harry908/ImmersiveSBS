# AI 3D improvement plan

Status (2026-10-19): in use. Since the status below, §10.2 (Video Depth Anything) was built as a
trial model and missed its go/no-go (M4 14 % lower through the depth service, against the 30 %
asked); it stays to be judged in the glasses. The buffer and matched timing (§6, §10) no longer
delay the picture in the app: the browser page delays picture and sound together and sends the
frames ahead for depth (`progress.md`, "Two layers"), so `VideoDelayBuffer`, named below, was
removed. Phase 4 steps 3 and 4, 5.2 and 6.2–6.3 are still not done.

Status (2026-10-10): **Phases 0, 1, 2, 3, E, 4 steps 0–2, 5.1, 6.1 and 6.4 implemented**,
plus §14's "Use page data", tested (Debug and Release), not yet seen in the glasses;
`progress.md` has what each does. Step 4's risk is cleared: `nvcuda.dll` exports the D3D11
interop calls. Phase 4 step 3 is done on the CPU's other cores (below). Not done, each waiting
on glasses numbers or a model export: Phase 4 step 4, Phase 5.2, Phase 6.2–6.3. Where the implementation departs from this plan, measured in tests:

- **§7, a slope limit added.** Soft edges alone still left 0.08–0.09 % holes for a step of 0.8;
  a screen-resolution cone dilation, n′(x) = max_d n(x+d) − g·|d| with g = 1.9/S, enforces
  §2.1's condition exactly before the splat. Holes: 0.13–0.34 % before, 0 after.
- **§7.2, coherence thresholds smoothstep(0.5, 0.8)**, not (0.7, 0.9): a gentle staircase only
  reaches C ≈ 0.8. Slope ⅓: RMS 0.274 → 0.081 px (0.165 with the plan's values).
- **§7.1 cost.** On this laptop's CPU in Release, a soft-edge pass is ≈ 1 ms and
  anti-aliasing ≈ 2 ms at 518 × 294, not "under 1 ms"; in a Debug build 5–7 times that. It runs
  on the post-processing thread (§9 step 1); §9 step 3 would remove it.
- **§8, span skipping off by default.** At σ 0.6 (a 17 px search) it cost more than it saved:
  1.09 ms against 0.84 ms a frame (the splat: 0.90 ms), at 1920 × 1080.
- **§5 and §9 step 0, the change test stays with the capture.** Dirty rectangles are exact per
  captured frame, while the page's frame events arrive on their own schedule; the frame events
  stamp media times, and the page's pause stops hand-overs.
- **§6, the matched delay includes the 20 ms present latency** (a frame is chosen that long
  before it is seen); the Auto rule compares μ + 2σ + T̄ without it.
- **§10.1, look-ahead in the depth thread** (it holds K frames before post-processing) rather
  than normalising at display time, so captions and overlays stay at the screen plane.
- **§9 step 3 done on the CPU's other cores instead of the GPU.** The first glasses run
  (2026-10-11) met its condition at 756 × 420: post 20–26 ms against a 9 ms model. The buffer and
  matched timing keep and interpolate depth on the CPU, so a GPU port would still read it back;
  splitting each step over bands of rows (at most 8 of the 20 threads) took it from 16.5 to
  5.7 ms there (8.5 → 3.3 ms at 518 × 294) with no change to the data flow or the output.
- **§6, sound only through the extension** (2026-10-11, user request): the process-loopback
  fallback is removed. Without the page, matched timing is capped at 90 ms with the sound live
  (the plan used Live there) and there is no buffer.
- **§11.4, the curve's parameters are our own** (softplus, k = 8, bias 0.15·L, L = −3..3;
  negative levels are the inverse curve), not iw3's named presets.

Written 2026-10-10. It builds on
`docs/2d-to-3d-research.md` (how iw3 and Depth3D work), the code as of the `browser-extension`
branch with the fp16 DA3 work on top, and the log of the first glasses run of that build
(2026-10-10, 06:00–06:20). All numbers below were computed from the formulas given next to
them (Python, in the session scratchpad); the formulas are here so they can be recomputed.

The plan is in phases; each is useful on its own and is checked before the next starts. No
phase removes a working path: every new method lands as an option beside the old one and is
compared before it becomes the default.

Where the video plays in Chrome or Edge with the ImmersiveSBS extension, the page itself
reports much of what the app now works out from pixels (§3). Each phase uses that wherever it
is there, and keeps today's method wherever it is not.

## 0. Goals and how they are measured

| # | Goal | Measure | Target |
|---|---|---|---|
| G1 | No tearing on moving edges, with any model | M1 depth age at display | 0 frames in matched mode; ≤ 1 depth period live |
| G2 | No dark or smeared shadow beside people and objects | M2 hole fraction of eye pixels | ≤ 0.2 % at σ = 0.6 (from several %, to be measured first) |
| G3 | No stair-stepped edges (doors, frames) | M3 edge straightness on the synthetic test scene | RMS deviation halved |
| G4 | Large models usable while the video plays | M1 + G1 | Shown in 3D, matched, no flat fallback |
| G5 | Smooth presentation | M5 GPU time per pass, present rate | 60 fps present; eyes ≤ 3 ms GPU per frame |
| G6 | Steadier depth | M4 flicker on still areas | Lower than now, measured per model |
| G7 | Nothing lost | Existing tests, subtitle lock, static overlays, buffer, theater | All pass and work |
| G8 | Less work where the page can tell | M7 model GPU share, read-back MB/s, tracker rate | With the extension: the model runs only on new video frames (40–60 % less model GPU time for small and base models at 24–30 fps); tracker at 4 Hz |

Measures (all logged in the 10 s `Stats:` line, or computed in tests):

- **M1 depth age:** time from a frame's capture to the depth shown with it, ms; mean and 95th
  percentile.
- **M2 hole fraction:** share of eye pixels that the warp left empty and the fill invented. A
  GPU counter, read back with the deferred read-backs.
- **M3 edge straightness:** in a GPU test, a straight diagonal and a vertical depth edge are
  given as staircase depth at model resolution. Warp, find the edge column in every row of
  the eye image, and take the RMS distance from the straight line, in screen px.
- **M4 flicker:** mean |nₜ − nₜ₋₁| over model pixels whose luma changed by less than 1.5/255.
  The scene is still there, so any depth change is noise.
- **M5 GPU time per pass:** D3D11 timestamp queries around Upsample, Snap, Dilate, Splat,
  Resolve and Present, read a few frames later, never waited on.
- **M6 depth worker split:** model, post-processing and waiting time per depth frame. Already
  in the status line since 2026-10-10.
- **M7 work share:** the depth model's busy share ($f \cdot T_m$), the share of hand-overs that
  repeat the previous video frame, and whether page data is in use (video followed, protocol
  version).

## 1. Starting point

### 1.1 Stereo geometry (RayNeo GT Max, 59° diagonal, 1920 × 1080 per eye)

Notation used throughout:

- $n \in [0, 1]$: nearness (1 = nearest).
- $c$: convergence, the nearness that stays at the screen plane.
- $\sigma$: the user's depth strength.
- $B$: the comfort budget, 1° of disparity.

$$\mathrm{HFOV} = 2\arctan\!\Big(\tan\tfrac{59°}{2}\cdot\tfrac{1920}{\sqrt{1920^2+1080^2}}\Big) = 52.50°,\qquad
B = 1°\cdot\tfrac{1920}{52.50°} = 36.6\ \text{px}$$

$$S_{\max} = \min\!\Big(\tfrac{B}{1-c},\ \tfrac{B}{c}\Big),\qquad S = \sigma S_{\max},\qquad
s(n) = \tfrac{S}{2}(n-c),\qquad x_L = x + s,\quad x_R = x - s$$

| c | σ | S (total px) | per-eye shift range (px) | hole search radius ⌈S⌉+2 |
|---|---|---|---|---|
| 0.35 | 0.6 (default) | 33.8 | −5.9 … +11.0 | 36 |
| 0.35 | 1.0 | 56.3 | −9.8 … +18.3 | 59 |
| 0.50 | 0.6 | 43.9 | −11.0 … +11.0 | 46 |
| 0.50 | 1.0 | 73.1 | −18.3 … +18.3 | 76 |

At the default, one unit of nearness moves a pixel 16.9 px in each eye. One model pixel is
3.71 screen px at model width 518 over a 1920 px video, and 2.54 px at width 756.

### 1.2 Measured

| Model | Speed test (ms) | In the app |
|---|---|---|
| DA3 Small 518 / 756 | 4.3–7.5 / 8.1–10.4 | — |
| DA3 Base 518 / 756 | 9.8–11.9 / 19.2–20.2 | — |
| DA2 Small 518 / 756 | 5.8 / 9.7 | — |
| DA2 / DAD Base 518 | 10.5 | — |
| DA2 Large 518 / 756 | 25.4 / 50.5 | shown flat by the stale-depth guard bug (fixed) |
| DAD Large 518×518 | 40.7 | 47.5 ms model, **12–13 depth frames/s**, lag 100–170 ms |

Other measurements from that run:
- **Capture and present:** about 96 captures/s, presents at 60 Hz, capture → present 9–10 ms.
- **Memory:** VRAM 1016 MB of a 5226 MB budget with DAD Large loaded.

### 1.3 The live latency model

The depth worker repeats a cycle of $T$ = model + post-processing. A frame handed over at a
random moment waits $T/2$ on average, is processed for $T$, then waits for the render loop
($v/2$, with $v$ = 16.7 ms at 60 Hz). Its input also reaches the worker only after the present
($\approx v$). So the mean depth age at display is

$$L \approx \tfrac{3}{2}T + \tfrac{3}{2}v .$$

**Check against the log:** DAD Large ran at 12.5 depth frames/s, so $T = 80$ ms and
$L \approx 145$ ms; the log showed 100–170 ms. The model alone is 47.5 ms, so **about 28 ms
per depth frame is unaccounted for** (Phase 0 finds it).

| Model | T (ms) | depth fps | mean L (ms) | worst ≈ 2T + 2v (ms) |
|---|---|---|---|---|
| DA3 S 518 | 9.3 | 107 | 39 | 52 |
| DA2 S 518 | 10.8 | 93 | 41 | 55 |
| DA3 B 518 | 14.8 | 68 | 47 | 63 |
| DAD B 518 | 15.5 | 65 | 48 | 64 |
| DA3 B 756 | 25.2 | 40 | 63 | 84 |
| DA2 L 518 | 30.4 | 33 | 71 | 94 |
| DAD L (model + 5 ms post) | 52.5 | 19 | 104 | 138 |
| DAD L (observed) | 80 | 12.5 | 145 | 193 |

(T assumes 5 ms of post-processing, until Phase 0 measures it.)

## 2. Two relations the plan rests on

### 2.1 When the forward splat leaves holes

`Splat` writes each source pixel to the columns within 0.99 px of its landing point
$x' = x \pm s(x)$, so each covers an interval just under 2 px wide. Two neighbouring source
pixels leave a gap between them exactly when their landing points are more than 2 px apart,
i.e. when $s(x+1) - s(x) > 1$ (left eye) or $< -1$ (right eye). With $s = \tfrac{S}{2}(n-c)$:

$$\text{no holes} \iff \Big|\frac{\partial n}{\partial x}\Big| \le \frac{2}{S}\ \text{per screen px}
\iff \text{a depth step } \Delta n \text{ is spread over at least } w = \Delta n\,\frac{S}{2}\ \text{px}.$$

| σ | S | Δn = 0.1 | Δn = 0.3 | Δn = 0.6 |
|---|---|---|---|---|
| 0.4 | 22.5 | 1.1 px | 3.4 px | 6.8 px |
| 0.6 | 33.8 | 1.7 px | 5.1 px | 10.1 px |
| 1.0 | 56.3 | 2.8 px | 8.4 px | 16.9 px |

Our current chain does the opposite of what this asks for:
- **`Snap`** sets $w \approx 0$ at every depth edge.
- **`Dilate`** is a hard maximum, so it keeps the step.

So every edge with $\Delta n \gtrsim 2/S = 0.06$ opens a hole, and `Resolve` invents its
content. That is the shadow (G2). iw3's soft edges make $w$ large enough for the warp to
stretch instead. Neither project states this relation; it is ours, and it gives the edge
treatment a number to aim for.

### 2.2 Showing each picture with its own depth

Live mode pairs the newest picture with depth that is $L$ old, so a moving edge is drawn where
it was $L$ ago (G1). Pairing exactly means delaying the picture by $D \ge L$. To give a shown
frame depth on **both** sides in time (so in-between frames can be interpolated rather than
held), one more depth period is needed:

$$D = L + T \approx \tfrac{5}{2}T + \tfrac{3}{2}v .$$

How much picture delay live sound can take, from ITU-R BT.1359 (sound ahead of the picture is
"+"):
- **detectable** from +45 ms;
- **acceptable** up to +90 ms (on the late side, −185 ms).

| Model | D (ms) | Sound |
|---|---|---|
| DA3 S 518 | 48 | live is acceptable |
| DA2 S 518 | 52 | live is acceptable |
| DA3 B 518 | 62 | live is acceptable |
| DAD B 518 | 64 | live is acceptable |
| DA3 B 756 | 88 | live is acceptable, just |
| DA2 L 518 | 101 | needs delayed sound |
| DAD L (if the 28 ms gap is fixed) | 156 | needs delayed sound |
| DAD L (now) | 225 | needs delayed sound |

## 3. The browser extension as a data source

Most videos watched in the glasses play in Chrome or Edge, where the extension (`extension/`,
`Browser/BrowserLink`) can read the page. Much of what the app now works out from pixels, the
page simply knows. This section lists what the page can report, what app work each signal
replaces, and what it cannot replace. The phases below use it wherever it is there, and keep
today's methods wherever it is not: other apps, other browsers, pages without a `<video>`.

### 3.1 What the extension does today (protocol 1)

Built on 2026-10-09; not yet seen in a browser.
- **Reports each media element:** its picture's box in the top-level viewport (through nested
  frames, inside the element's letterbox), whether it plays, whether it is audible, a seek
  counter, whether its sound can be delayed in the page, and the delay applied.
- **App side:** the box replaces the tracker's while the two agree; a seek clears the depth
  history; the theater's Play/Pause toggles the element.
- **Sound:** the page delays it through Web Audio (element → `DelayNode`) when the buffer is on.
- **Cost while the app is closed:** nothing is measured or sent.

### 3.2 What a page can report

| Signal | Browser API | Rate | Have |
|---|---|---|---|
| Video picture box | `getBoundingClientRect` + `object-fit` maths, frame offsets by message | on change, checked 2/s | yes |
| Playing, paused, seeking, rate, ended | media events, `playbackRate` | on change | partly (playing, seeks) |
| **Each presented video frame** | `requestVideoFrameCallback`: `presentedFrames`, `mediaTime`, `expectedDisplayTime`, frame size | per video frame (24–60/s) | no |
| **Page clock vs. app clock** | `performance.now` against the app's clock, by ping | every 5 s | no |
| **Caption boxes** | the player's caption elements (DOM-rendered captions), `textTracks` cues showing | on change | no |
| **Overlay boxes** (controls, logos, titles, ad badges) | `elementsFromPoint` on a grid over the video | 4/s | no |
| Tab shown, full screen | `visibilityState`, `fullscreenElement` | on change | yes |
| Delayed sound | Web Audio `DelayNode` per element | — | yes |

### 3.3 What each signal replaces

Numbers: §1.1 geometry, 518 × 294 model input, 1920 × 1080 video, 60 Hz render loop.

| App work today | With the extension | Saving | Still needed without it, or when the page cannot tell |
|---|---|---|---|
| Luma grid and region tracker on every captured frame: 2.1 M texel reads, a 127 KB read-back, and 32,400 cells of energy, threshold, closing and labelling (1.94 M cell updates/s) | The page's box; the tracker drops to a 4 Hz cross-check | ~93 % of that work; **no box jitter**, so no live depth reset when controls appear | Other apps; native-3D detection (low rate is enough) |
| **A model input handed over on every render loop (60/s)**, whether or not the video changed | Only when `presentedFrames` advances (a new video frame) | Model GPU time: see the table below. Read-back 104 → 42–52 MB/s | Without the extension: Desktop Duplication's dirty rectangles, or the luma grid's change, over the region (§9) |
| Pause found by the tracker after 3 s of no change; **never in the theater**, where every frame counts | The `pause` event | **All** model work while paused, from the first frame | Other apps |
| Scene reset by a luma threshold, after the cut | The `seeking` / `seeked` events | A clean reset at the jump; no smear across it | Cuts within the video still need the luma test |
| Buffer frames pushed at the loop rate | Pushed only for new video frames | Memory about halved (below); a 2 s buffer fits without dropping frames | Other apps |
| Matched-mode timing from capture times | Video frame times with clock sync | Exact λ (§6); the delay counted in video frames | Capture times |
| Delayed sound by process loopback (capture thread, playback, turning the app down) | In-page delay | All of `AudioDelay`'s work; full quality; exact delay even for tens of ms | Other apps |
| Subtitle detection on every depth frame (CPU; the run logged a false box flipping for minutes) | The page's caption boxes | That detection, where captions are page elements; **no false boxes** | Subtitles burned into the picture |
| Static-overlay detection (per pixel, needs 1 s of stillness) | Overlay boxes from the page | Immediate and exact for the player's own controls, titles and logos | Logos burned into the picture |

Model GPU time spent on frames it has already seen (busy $= \min(1, f\cdot T_m)$, at $f = 60$
hand-overs/s now and $f = f_{\text{video}}$ with the extension):

| Model (ms) | Video fps | Busy now | Busy with frame events | Share now spent on repeats |
|---|---|---|---|---|
| DA2 S 518 (5.8) | 24 / 30 | 35 % | 14 % / 17 % | 60 % / 50 % |
| DA3 B 518 (9.8) | 24 / 30 | 59 % | 24 % / 29 % | 60 % / 50 % |
| DAD B 518 (10.5) | 24 / 30 | 63 % | 25 % / 32 % | 60 % / 50 % |
| DA3 B 756 (20.2) | 24 / 30 | 100 % | 49 % / 61 % | 52 % / 39 % |
| DAD L (47.5) | 24 / 30 | 100 % | 100 % | 0 % (already slower than the video) |

For small and base models, half or more of the depth model's GPU time today goes on frames it
has already seen. That GPU time competes with capture, the warp and the present.

Buffer memory, held frames $= \lceil D f\rceil + 8$ at 8.29 MB each, against the 600 MB cap:

| Delay | 60 pushes/s | 30/s | 24/s |
|---|---|---|---|
| 0.15 s (matched) | 134 MB | 103 MB | 95 MB |
| 1 s | 538 MB | 301 MB | 253 MB |
| 2 s | 1012 MB, over the cap, so frames are dropped | 538 MB | 443 MB |

### 3.4 What it cannot replace

- **Depth inference:** the page has no depth.
- **The delayed picture** (`VideoDelayBuffer`):
  - **The page could delay its own video**, drawing late frames over the element. But then the
    app would capture only late frames and lose the look-ahead the depth needs.
  - **Sending the live frames to the app instead** would be about
    $1920\cdot1080\cdot4\cdot30 \approx 249$ MB/s raw, or a video encoder and decoder in the
    loop.

  So the picture is delayed in the app. Only the rate it holds frames at drops.
- **Text and logos burned into the picture:** these are pixels, not page elements.
- **Video that is already 3D:** a property of the content, not the page.
- **Other players:** desktop apps (VLC, MPV), players drawing to a canvas or WebGL without a
  `<video>`, and DRM video (out of scope).

### 3.5 Trust and fallback

The page's data is a hint, checked cheaply, and the app falls back to today's behaviour the
moment a check fails:

1. **Box:** the 4 Hz tracker must agree, by intersection over union ≥ 0.5, whenever it sees a
   video. On disagreement for 1 s, the tracker's box is used again.
2. **Freshness:** no message about the followed video for 1 s means no page data.
3. **Frame events:** only trusted while they agree with the capture. If the captured region
   keeps changing but `presentedFrames` does not, for 0.5 s, the capture decides.
4. **Origin:** only the extension's own origin connects (already).

## 4. Phase 0: measure first

**Why:** two of the plan's numbers are guesses: post-processing time, and where DAD Large's
missing 28 ms go. And no measure yet exists for holes, edge straightness or GPU time per
pass. Every later phase is judged by these measures.

**Work:**
1. **M5 GPU timestamps** around each `StereoSynth` pass and the present. A disjoint query
   brackets the frame, and the results are read 3 frames later, never waited on.
2. **M2 hole counter:** `Resolve` adds 1 to a counter per hole pixel. It is read once a second
   with the deferred read-backs.
3. **M4 flicker** in `DepthPostProcessor`; it costs one pass over pixels already being read.
4. **M1 depth age:** capture time of the frame shown minus capture time of the depth's frame,
   mean and p95.
5. **M3 straightness test scene** in the GPU tests: a staircase diagonal edge (DA2-like,
   nearest-neighbour steps of 1 model px) and a vertical door edge, at 518 × 294, warped to
   1920 × 1080.
6. **Read M6** (already logged) on a glasses run with DAD Large, and split the 28 ms between
   post-processing and waiting.
7. **M7** in the `Stats:` line, with the page-data state: extension connected, video followed,
   protocol version.

**Done when:** one glasses run with three models (DA2 S, DAD B, DAD L) gives every measure,
once in the browser with the extension and once without it, and the table in §1.3 is updated
with measured post-processing times.

**Effort:** small (1–2 sessions).

## 5. Phase E: what the extension reports (protocol 2)

**Why:** §3.3. These signals let Phases 1, 2, 4 and 5 drop work and be exact where the page
knows. This phase adds the reporting; the phases that use it say how.

**Design:**

1. **Messages.** The hello carries the protocol version; the app still accepts version 1 with
   fewer features. New messages:
   - **app → page `follow`:** the media id the app follows. Only that element sends frame and
     overlay data, which keeps the traffic small.
   - **page → app `frame`:** per presented video frame. Carries `presentedFrames`,
     `mediaTime`, `expectedDisplayTime` and the frame size; about 120 bytes, at 24–60 per
     second, so 3.5–7 KB/s.
   - **page → app `state`:** paused, seeking, seeked, rate, ended, visible, full screen; sent at
     once on change.
   - **page → app `overlays`:** boxes over the video, each with a kind (caption, control,
     other); at most 4/s and only when changed, about 2 KB, so under 8 KB/s.
   - **`clock` ping and pong:** see 2.
2. **Clock sync**, NTP-style. The page sends at page time $t_0$; the app receives at app time
   $t_1$ and answers at $t_2$; the page receives at $t_3$. Then
   $$\theta = \frac{(t_1 - t_0) + (t_2 - t_3)}{2},\qquad \delta = (t_3 - t_0) - (t_2 - t_1),\qquad |\theta - \theta_{\text{true}}| \le \delta/2 .$$
   - **Sampling:** keep the sample with the smallest $\delta$ of the last 8, re-synced every
     5 s. Page times map to the app's clock as $t_{\text{app}} = t_{\text{page}} + \theta$.
   - **Accuracy:** on the loopback connection, $\delta$ is about 0.3–3 ms, so the error is at
     most 0.15–1.5 ms. That is 1–9 % of a 60 Hz frame.
   - **Drift:** both clocks are the system's high-resolution counter, so drift is negligible
     between re-syncs.
3. **Which video frame a capture shows.** A captured frame is a *new video frame* when the
   latest frame event's `presentedFrames` has advanced since the previous capture. Its video
   time is that event's `mediaTime`, and it was shown at about
   $t_{\text{app}}(\text{expectedDisplayTime})$.

   A constant compositor delay $\kappa$, between the page presenting a frame and Desktop
   Duplication delivering it, is calibrated once per session. Take the median, over 2 s, of
   capture time minus mapped display time for frames where the region visibly changed.
4. **Overlay probe**, in the frame that holds the video.
   - **Probe:** `elementsFromPoint` at a $g \times g$ grid of points over the picture.
     $g = 8$: 64 probes every 250 ms, 256 calls/s.
   - **What counts as an overlay:** any hit above the video, other than the video itself and
     its transparent wrappers, that has visible content: text, an image, SVG or canvas, or a
     non-transparent background, with opacity > 0. Hits on one element merge into its box.
     A box over more than 50 % of the picture is ignored: players lay full-size transparent
     layers over the video to catch clicks.
   - **Kind "caption":** the player's caption containers. A short list of known selectors
     covers YouTube, Video.js, Plyr, JW Player, Vimeo and Twitch. Native `<track>` captions
     (`textTracks` with a cue showing) add a caption box at the browser's default place, the
     bottom centre, sized from the cue's line count.
5. **App side.**
   - **`BrowserMedia`:** carries the last frame event, state and overlays.
   - **`AppController`:** keeps the clock offset and sends `follow`.
   - **`Session` and `AiStereo`:** receive one snapshot per frame: box, new-frame flag, video
     time, state, overlay boxes in source px.

**Verification:**
- **Tests:**
  - **Version 2 messages:** parsing and rejection of malformed ones.
  - **Clock estimator:** with synthetic one-way delays of 0.1–5 ms and jitter, the error is
    within $\delta/2$.
  - **New-frame flag:** follows `presentedFrames`, and falls back by rule 3 of §3.5.
  - **Overlay boxes:** map to a model-resolution mask exactly.
- **Browser:** on YouTube, the log lists the followed video's frame rate, clock δ and the
  overlay boxes found (captions, control bar).

**Done when:** on YouTube with the extension:
- the model runs at the video's frame rate, and its GPU share falls as §3.3's table predicts
  (within 10 points);
- captions come from the page;
- no false subtitle box appears in a session.

**Effort:** medium (3–4 sessions).

## 6. Phase 1: matched timing

**Why:** G1 and G4. Tearing comes from depth that is $L$ old (§2.2); the buffer already shows
each frame with its own depth, but only at 0.5–2 s and only with delayed sound.

**Design:**
- **A third timing mode**, beside Live and Buffer: *Matched*. The picture is delayed by $D$,
  using the existing `VideoDelayBuffer` (each held frame keeps its crop and its depth).
- **$D$ adapts to the measured depth age.** Keep an exponential average of each depth frame's
  age, mean $\mu_L$ and standard deviation $\sigma_L$ (time constant 2 s), and the mean
  period $\bar T$. Then
  $$D^\ast = \mu_L + 2\sigma_L + \bar T,\qquad D \in [0, 300]\ \text{ms}.$$
  $D$ follows $D^\ast$ only when they differ by more than 20 ms for 2 s. Every change of $D$
  briefly holds or skips the picture, so it must be rare.
- **In-between frames:** a shown frame $f$ at time $t_f$ between depth frames $a$ and $b$
  ($t_a \le t_f \le t_b$) gets
  $$n_f = (1-\lambda)\,n_a + \lambda\,n_b,\qquad \lambda = \frac{t_f - t_a}{t_b - t_a}.$$
  This replaces the 1-2-3-2-1 neighbour average in matched mode; the buffer keeps its average,
  which also smooths. A linear blend of two depth maps cross-fades a moving edge instead of
  moving it. That is softer than one exact position, but never a tear. Motion-compensated
  interpolation is a later option, not part of this phase.
- **Sound:**
  - $D \le 90$ ms: the sound plays live.
  - $D > 90$ ms: matched mode needs the delayed-sound path. With the extension, the page
    delays by exactly $D$; without it, process loopback.
  - Neither available: show Live, with a status line saying why.
- **Only the video region is delayed**, as with the buffer; the cursor and the rest of the
  desktop stay live.
- **The stale-depth guard** (`AiStereo` live path) does not apply in matched mode: the depth
  age at display is zero by construction.
- **Default:** Matched when $D^\ast > 60$ ms (large models, Quality sizes). Live when it is
  smaller, where the age is one or two video frames.

**With the extension** (Phase E):
- **Frame times:** $t_f$, $t_a$, $t_b$ become the video's own media times, so
  $\lambda = (m_f - m_a)/(m_b - m_a)$ is exact even when captures arrive unevenly. $D$ is
  counted in video frames:
  $D_{\text{frames}} = \lceil(\mu_L + 2\sigma_L + \bar T)\,f_{\text{video}}\rceil$.
- **Sound:** always delayed in the page by exactly $D$, below 90 ms too, so lip sync is exact
  rather than merely acceptable. The 90 ms rule applies only without the extension.
- **Pause:** the held frames stay. The page's delay line plays out its $D$ of sound as the
  picture plays out its $D$ of frames, so both stop together (already true for the buffer).
- **Seek:** the held frames and the depth history are cleared at the `seeking` event; nothing
  is blended across the jump.
- **Memory:** frames are pushed at the video's rate (§3.3): 95–103 MB at $D$ = 0.15 s instead
  of 134 MB.

**Cost:**
- **Memory:** $\lceil 60D\rceil + 8$ held frames of the region; at 1920 × 1080:

  | D (ms) | 50 | 90 | 150 | 250 |
  |---|---|---|---|---|
  | GPU memory (MB) | 87 | 111 | 134 | 182 |

  This is well inside the existing 600 MB cap and the 5.2 GB budget.
- **Compute:** the same as the buffer path.

**Verification:**
- **Tests:**
  - frame selection under a fixed $D$ returns frames whose bracketing depth frames exist;
  - λ at the two ends gives exactly $n_a$ and $n_b$;
  - $D$ moves only under the hysteresis rule.
- **Log:** M1 = 0 in matched mode; $D$ and $D^\ast$ in the `Stats:` line.

**Done when:** DAD Large plays in 3D on a moving scene with no flat fallback and no tearing
visible at door and body edges, with $D$ near the §2.2 value for the measured $T$.

**Effort:** small to medium (2–3 sessions). The buffer machinery exists.

## 7. Phase 2: the edge pipeline

**Why:** G2 and G3. §2.1 shows the current chain turns every depth edge into a hole. The
research shows DA2 and Video Depth Anything give stair-stepped edges (iw3 issue #406), and
DA3 probably does too *(inference)*.

**New order of operations at model resolution** (all after normalisation and temporal
smoothing, and before the subtitle and static-overlay overrides, so text stays flat):

1. Depth anti-aliasing (DA2 and DA3 only).
2. Soft edges.
3. Static overlays and subtitles, as now.

Both new steps change only the output, never the temporal state, as the overlays already do,
so switching them off restores the model's own depth at once.

### 7.1 Soft edges (iw3 "Edge Fix")

On the nearness map $N$ at model resolution, one pass is:

$$r = \max_{3\times3}N - \min_{3\times3}N,\qquad
z = \mathrm{clip}\!\Big(\frac{r-\bar r}{\operatorname{sd}(r)},\,-3,\,3\Big),\qquad
w = \frac{z - \min z}{\max z - \min z}$$

$$N \leftarrow (1-w)\,N + w\cdot\max_{3\times3}\!\big(G * N\big),\qquad
G = \tfrac{1}{256}\begin{pmatrix}21&31&21\\31&48&31\\21&31&21\end{pmatrix}$$

- $G$ sums to 1 and is close to a Gaussian of σ ≈ 0.76 px.
- The maximum grows the near side by about one model pixel per pass, and the blur keeps the
  ramp soft.
- $w$ is near 0 on flat depth, so only edges change.

**How many passes:** choose $k$ so the ramp covers §2.1's width for a typical edge,
$\Delta n_{\text{ref}} = 0.4$:

$$k = \operatorname{clamp}\!\Big(\Big\lceil \frac{\Delta n_{\text{ref}}\, S/2}{W_{\text{region}}/W_{\text{model}}}\Big\rceil,\ 1,\ 4\Big)$$

| σ | S | Model width 518 | Model width 756 |
|---|---|---|---|
| 0.4 | 22.5 | k = 2 | k = 2 |
| 0.6 | 33.8 | k = 2 | k = 3 |
| 0.8 | 45.0 | k = 3 | k = 4 |
| 1.0 | 56.3 | k = 4 | k = 4 |

This agrees with iw3's default of 2 and its "4 is the most eye-friendly". Disparity is
horizontal, so passes are counted separately along x and y (iw3 allows this too): $k_x$ from
the formula, $k_y = 1$. The setting "Edge softness" chooses Auto (the formula), or 0–4.

**Cost:** about 27 operations per pixel per pass on 152 k pixels (518 × 294), on the CPU in
`DepthPostProcessor`; under 1 ms per pass, and M6 confirms it. If Phase 4 moves
post-processing to the GPU, it moves with it.

### 7.2 Depth anti-aliasing for DA2 and DA3 (Depth3D's method, no network)

Smooth along each edge and never across it.
- **Gradient:** Sobel on $N$.
- **Structure tensor:**
  $J = G_\rho * \begin{pmatrix} N_x^2 & N_xN_y \\ N_xN_y & N_y^2 \end{pmatrix}$, $\rho = 1$
  model px.
- **Eigenvalues** $\lambda_1 \ge \lambda_2$; coherence
  $C = \big(\tfrac{\lambda_1-\lambda_2}{\lambda_1+\lambda_2}\big)^2$; tangent $t$, the
  eigenvector of $\lambda_2$.
- **Filter:** a 9-tap Gaussian along $t$, $\sigma = 2$ model px:

  $$N'(p) = \frac{\sum_{k=-4}^{4} g_k\, N(p + k\,t)}{\sum g_k},\qquad g_k = e^{-k^2/8}
  = 1,\ 0.8825,\ 0.6065,\ 0.3247,\ 0.1353$$

  Folded into two bilinear taps per side, as Depth3D does (verified): offsets 1.40733 and
  3.29421, weights 1.48903 and 0.45999, normalisation $1/(1 + 2(1.48903 + 0.45999)) = 0.204164$.
  That is 5 taps per pixel instead of 9.
- **Applied with weight** $\beta = \operatorname{smoothstep}(0.7, 0.9, C)$ where
  $|\nabla N| > g_{\min}$, else 0. Only straight, clear edges are smoothed; corners and texture
  are left alone.

**When:** on for DA2 and DA3, off for Distill-Any-Depth, whose output iw3 found already smooth.
The setting is Auto, On or Off.

**Order relative to 5.1:** anti-alias first, then soften. iw3's author found soft edges hide
most of the stair-stepping. So after 5.1 lands, M3 decides whether 5.2 earns its place: keep
it only if it improves M3 by more than 20 % beyond 5.1 alone.

### 7.3 What changes in the screen-resolution passes

- **Screen-pixel `Dilate`:** default radius 0 (off). The near side now grows at model
  resolution, softly.
- **`Snap`:** default off for DA2 and DA3. For Distill-Any-Depth it is an A/B setting, off
  until M2 and M3 say otherwise. Snapping is right only with a warp that handles the
  resulting step, which is Phase 3.
- **Colour-guided `Upsample`:** kept. Its colour term re-sharpens ramps that cross a colour
  edge, so its range σ (now 0.1) becomes part of the edge setting: 0.1 at softness 0, rising
  to 0.3 at softness 4. That keeps the ramp §2.1 needs.

### 7.4 Flat regions from the page (with the extension)

- **Overlay boxes** from Phase E (source px) are mapped to model resolution and held at the
  screen plane the way static overlays are. A mask $M$ rises with τ = 0.1 s and fades with
  τ = 0.4 s (as in `CaptionLock`), and $N \leftarrow N + (c - N)\,M$. It is applied after the
  soft edges, so the boxes stay flat and their edges sharp.
- **Caption boxes** go through `CaptionLock`'s depth rule instead: in front of the 90th
  percentile of what is behind them, plus 0.03. They replace its detection while the page
  reports captions for the followed video. Detection stays on when the page reports none
  (subtitles burned into the picture).
- **Player controls** held flat also stop the model's guess at them flickering as they appear
  and fade.

**Verification:**
- **Tests:**
  - **Hole count:** a step $\Delta n = 0.3$ at σ = 0.6 with $k$ from the formula gives M2 = 0
    in the eye images. Today's chain gives holes over about 5 px per row.
  - **Flat areas untouched:** a flat region is unchanged by both steps.
  - **Subtitles stay flat:** a subtitle box keeps its flat depth.
  - **M3:** halved on the staircase scene.
  - **Page boxes:** a box given in source px lands on the right model pixels, through the
    region's crop and the model's aspect ratio.
- **Glasses:** the shadow and jagged-door cases from the 2026-10-10 run, A/B in the tuning
  panel.

**Done when:** G2 and G3 targets met on the test scene, and the glasses A/B prefers the new
default on DA2 and DA3.

**Effort:** small to medium (2–3 sessions).

## 8. Phase 3: a lookup warp

**Why:** with the forward splat, sharp correct edges always open holes (§2.1). A lookup
("gather") warp, as Depth3D and iw3 use, finds for every output pixel the source pixel that
lands there. Nothing is left empty, and near surfaces correctly hide far ones. This makes
sharp, snapped edges usable again, and removes the hole fill and its artifacts.

**Design (left eye; the right eye is the mirror image):**

- **The visible source pixel.** For output column $u$, it is the **smallest** root of
  $$g(x) = x + s\big(n(x)\big) - u\quad\text{on}\quad [\,u - s_{\max},\ u - s_{\min}\,],
  \qquad s_{\max} - s_{\min} = S/2 .$$
  Reason: if $x_1 < x_2$ both land on $u$, then $s(x_1) - s(x_2) = x_2 - x_1 > 0$, so $x_1$
  is nearer and hides $x_2$. For the right eye, $g(x) = x - s(n(x)) - u$ and the **largest**
  root is visible.
- **March.** From $x_0 = u - s_{\max}$ rightwards in steps of $h = 1/q$ px, with
  $N = \lceil qS/2 \rceil$ steps, until $g$ changes sign. Then refine linearly between the
  last two samples:
  $$x^\ast = x_{k-1} + h\,\frac{-g_{k-1}}{g_k - g_{k-1}} .$$
- **Skipping empty spans** (Depth3D's "nearest depth chain", exact). Per row, keep the maximum
  nearness $M_j$ over runs of 16 px. A run $[a_j, b_j]$ cannot contain a root when
  $$b_j + s(M_j) < u,$$
  so the march jumps the whole run. The maxima are one extra pass, at 1/16 of the width.
- **Disocclusions.** Where the two bracketing samples differ by more than $\tau_e = 0.1$ in
  nearness, the crossing is at a depth edge and the gap shows what was hidden. Take the
  sample of the **farther** surface (stretch the background, not the object), blended over
  $\tau_e \ldots 2\tau_e$ so it does not flicker. This is Depth3D's default "Alpha" behaviour.
  An infill blur over the stretched span is an option later.
- **Floating window and both eyes:** as now.

**Cost** (texture fetches, both eyes, 1920 × 1080, before skipping):

| σ | Range per eye | Quality q = 0.625 | Quality q = 1.0 |
|---|---|---|---|
| 0.6 | 16.9 px | 11 steps, 54 M/frame, 3.2 G/s at 60 fps | 17 steps, 79 M/frame, 4.7 G/s |
| 1.0 | 28.1 px | 18 steps, 83 M/frame, 5.0 G/s | 29 steps, 129 M/frame, 7.7 G/s |

- **GPU load:** 2–5 % of the RTX 3060 Laptop's texture rate (≈ 168 Gtexel/s from 120 units at
  ≈ 1.4 GHz, a spec estimate; M5 gives the real time).
- **Skipping:** cuts the steps over flat and far areas by up to 16×.
- **What goes:** the splat's 8 M atomic writes per frame, the hole search, and the 16.6 MB key
  buffer.

**Verification:**
- **Tests:**
  - **Occlusion:** a near box over a far background hides it, and nothing is empty.
  - **Disocclusion:** the revealed strip takes background colour, never the box's.
  - **Opposite shifts:** uniform nearness shifts the two eyes oppositely by $S/2(n-c)$, to
    within 0.1 px, so the existing tests pass in both modes.
  - **No missed roots:** skipping gives the same output as no skipping.
  - **Stair-steps:** M3 on the staircase scene.
- **Glasses:** A/B against splat plus Phase 2 soft edges, in the tuning panel ("Warp: Splat /
  Lookup").

**Done when:** M2 = 0 by construction, M5 ≤ 3 ms for the eyes at σ = 0.6, and the glasses A/B
prefers it. Then decide whether snapping returns for the lookup warp.

**Effort:** medium to large (4–6 sessions).

## 9. Phase 4: depth path performance

**Why:** G4, G5 and G8.
- DAD Large loses about 28 ms per depth frame outside the model (§1.3).
- For small and base models, half or more of the model's work repeats frames it has already
  seen (§3.3).
- Every depth frame crosses the CPU twice: the input read back, then the post-processed depth
  uploaded.

**Steps, each decided by Phase 0's numbers:**

0. **Hand over only new video frames.** Today a model input is queued on every render loop that
   captured anything, 60 a second, while a 24–30 fps video changes only every second or third
   loop.
   - **Rule, first that applies:**
     - with the extension, hand over iff `presentedFrames` has advanced since the last
       hand-over;
     - without it, iff Desktop Duplication's dirty rectangles for the frame (delivered with
       it) intersect the region;
     - otherwise, iff the luma grid's mean change in the region exceeds 1/255.
   - **Paused or hidden:** no hand-overs at all. That includes the theater, where today every
     frame counts.
   - **Same rule** for the buffer's and matched mode's pushes.
   - **Saving:** the model GPU time in §3.3's table, and half the read-back (104 → 42–52 MB/s
     at 518 × 294). It also makes the stale-depth guard count video frames, as it was meant
     to.
   - Small. It comes first in this phase, because it frees GPU time for everything else.
1. **Overlap post-processing with the next inference.** While the GPU runs frame $k{+}1$, the
   CPU post-processes frame $k$. Throughput goes from $1/(m+p)$ to $1/\max(m,p)$:

   | Model (ms) | Post (ms) | Depth fps now | Overlapped |
   |---|---|---|---|
   | 47.5 | 5 | 19.0 | 21.1 |
   | 47.5 | 28 | 13.2 | 21.1 |
   | 10.5 | 5 | 64.5 | 95.2 |
   | 10.5 | 15 | 39.2 | 66.7 |

   Latency does not get worse: each frame is still model, then post. Worth doing if M6 shows
   $p \gtrsim 0.3\,m$.
2. **If the time is waiting, not post-processing.** The worker idles until the render loop
   hands over a frame, after the present. Hand the input over as soon as its copy is ready (a
   query polled each loop) instead of after the present.
3. **Move post-processing to the GPU,** as compute passes at model resolution:
   - normalisation (two percentiles by histogram, which needs no sort);
   - temporal smoothing;
   - soft edges and anti-aliasing;
   - static overlays.

   The subtitle detector stays on the CPU on a small luma copy, or moves later. This removes
   the depth upload and the CPU time.
4. **Keep the model's input on the GPU.** Register the D3D11 input buffer with CUDA
   (`cudaGraphicsD3D11RegisterResource`, as iw3's `wc_cuda` does for capture) and hand it to
   TensorRT through ONNX Runtime IOBinding. The output comes back the same way. Bytes saved
   per depth frame:

   | Model size | Input (MB) | Depth (MB) | Transfer at 16 GB/s (ms) |
   |---|---|---|---|
   | 518 × 294 | 1.74 | 0.58 | 0.11 + 0.04 |
   | 756 × 420 | 3.63 | 1.21 | 0.24 + 0.08 |
   | 518 × 518 | 3.07 | 1.02 | 0.20 + 0.07 |

   The transfers themselves are small; the gain is the synchronisation and the CPU's part. So
   this step comes last, and only if steps 1–3 leave the depth path as the bottleneck.

   **Risk:** whether a CUDA runtime is available beside the TensorRT for RTX package. Check
   that first, in a one-session spike.

**Done when:** DAD Large reaches ≥ 19 depth fps in the app (its model-only rate), with M6
showing waiting < 5 ms.

**Effort:** steps 1–2 small; step 3 medium; step 4 large.

## 10. Phase 5: steadier depth

1. **Look-ahead normalisation in the buffer** (iw3's EMA min–max with a look-ahead buffer).
   With buffer delay $D$ and depth rate $f_d$, $K = \lfloor D f_d \rfloor$ future depth frames
   are already known when a frame is shown. Normalise with
   $$\ell_t = \mathrm{EMA}_\tau\!\Big(\min_{t \le u \le t+K} p_2(u)\Big),\qquad
   h_t = \mathrm{EMA}_\tau\!\Big(\max_{t \le u \le t+K} p_{98}(u)\Big),\qquad
   n = \operatorname{clip}\!\Big(\frac{r - \ell_t}{h_t - \ell_t}, 0, 1\Big).$$
   The range opens before a near object enters instead of after. No pumping, no clipping.
   This needs each buffered frame's relative depth and percentiles, with normalisation done
   at display time (in `AiStereo`, not the depth thread). For example, at $D$ = 1 s and 20
   depth fps, $K$ = 20 frames. With the extension, the window is in video time, and a seek
   resets it at once.
   - **Our current smoothing per frame**, for reference: percentile average
     $\alpha = 1 - e^{-\Delta t/0.5}$ = 0.033 at 60 Hz and 0.148 at 12.5 Hz. Per-pixel minimum
     $\alpha_{\min} = 1 - 0.8^{30\Delta t}$ = 0.106 at 60 Hz and 0.415 at 12.5 Hz. A slow
     model already smooths less per frame, by design.
2. **Video Depth Anything Small, streaming (a spike).** Apache-2.0; one frame per call with
   cached temporal state. Export the encoder and the streaming head with the caches as
   explicit inputs and outputs, kept on the GPU with IOBinding (after Phase 4 step 4).

   **Go only if both hold:**
   - ≤ 25 ms per frame at 518 on this GPU;
   - M4 improves ≥ 30 % against DAD Base without M3 getting worse.

   Its authors report a quality drop for streaming without fine-tuning. Its caches reset on
   the page's seek events.
3. **Scene cuts:** keep the luma threshold. The look-ahead in 1 also handles cuts in the
   buffer, since a cut's range shows up $K$ frames early. With the extension, a seek is a
   known cut.

**Effort:** 1 medium; 2 is a research spike (3–5 sessions) with a go/no-go.

## 11. Phase 6: smaller items

1. **Subtitle false positive.** The run logged a box at x = 323, width 180, model width 518,
   flipping between found and gone for minutes. Its centre is 413, inside today's band
   $(W/5,\ 4W/5) = (103.6,\ 414.4)$ by under 2 px.
   - **Fix:** narrow the band to $(W/4,\ 3W/4) = (129.5,\ 388.5)$. The real subtitle boxes in
     the same log (centres 196, 190, 284, 132, 259) all stay inside; 132 by only 2.5 px, so
     left-aligned subtitles are a risk.
   - **Also require** the box to stay found for 1 s before a second appearance counts within
     5 s, which stops the flip-flopping.
   - This matters only where the page does not report captions (§7.4): burned-in subtitles,
     other players.
2. **Automatic convergence** (iw3's subject finder): put the main subject at the screen plane.
   Needs a small salient-object model; later, after Phases 1–3.
3. **Remembered background** (Depth3D's Memory Infill): fill disocclusions from earlier frames
   where the background was visible. Needs motion vectors; only if the lookup warp's stretch
   still shows.
4. **A foreground-scale curve** (iw3's softplus mapper) as a tuning option, for scenes where
   people look flat.

## 12. Order and dependencies

```
Phase 0 (measure) ─┬─► Phase 1 (matched) ──► Phase 2 (edges) ──► Phase 3 (lookup warp)
                   ├─► Phase E (extension data) ──► feeds Phases 1, 2, 4 and 5
                   └─► Phase 4 (depth path, step 0 first) ─────────────► Phase 5 (temporal)
                                                                         Phase 6 (any time)
```

| Phase | Effort | Fixes (from the glasses run) |
|---|---|---|
| 0 | small | — (makes the rest measurable) |
| E | medium | less GPU and CPU work in the browser; exact timing; captions and controls flat |
| 1 | small–medium | tearing on moving edges; large models |
| 2 | small–medium | shadows; jagged door edges |
| 3 | medium–large | remaining hole artifacts; makes sharp edges possible |
| 4 | small → large | large-model depth rate; latency |
| 5 | medium + spike | flicker, depth pumping |
| 6 | small each | subtitle flicker; comfort; flat people |

Phases 1 and 2 together cover all three artifacts from the glasses run, and are the
recommended first step after Phase 0. With them, do Phase 4 step 0: its fallback works
without the extension, and it frees GPU time for everything else. Phase E comes next, because
it makes Phases 1, 2 and 4 exact and cheaper wherever the page can tell.

## 13. Risks

| Risk | Effect | Mitigation |
|---|---|---|
| Linear depth blending in matched mode cross-fades fast edges | Soft double edge on very fast motion | Live and matched selectable; motion-compensated blending later |
| Soft edges make objects look "fat" (iw3: "degrades depth accuracy") | Background next to people moves with them | Strength-tied $k$ (§7.1), $k_y = 1$, setting 0–4 |
| Matched delay over 90 ms without the extension or loopback | Lip-sync visible | Rule in §6: Live is used instead, with a status line |
| The lookup warp's disocclusion choice flickers | Shimmer at edges | Blend over $\tau_e \ldots 2\tau_e$; temporal stability test on a moving edge |
| No CUDA runtime for D3D11 interop | Phase 4 step 4 impossible | Spike first; steps 1–3 do not depend on it |
| VDA streaming quality drop | No gain | Go/no-go on M3 and M4 |
| More options confuse | Hard to tune | Auto defaults everywhere; options only in the tuning panel |
| Page data wrong (box offset on a normal window, page zoom, cross-origin frames) | Video cropped wrong | §3.5: the 4 Hz tracker must agree, data must be fresh, else fall back |
| Frame events stop (background-tab throttling, the player swaps its element) | The model starves | §3.5 rule 3: after 0.5 s the capture decides |
| The overlay probe takes a player's full-cover transparent layer for an overlay | The whole video goes flat | Only elements with visible content count; a box over more than 50 % of the picture is ignored |
| Players without a `<video>` (canvas, WebGL), or other browsers | No page data | Today's methods stay; nothing depends on the page |

## 14. Settings that change

| Setting | Where | Values | Default |
|---|---|---|---|
| Timing | Settings, theater panel | Live / Matched / Buffer 0.5–2 s | Matched when $D^\ast > 60$ ms, else Live |
| Edge softness | Tuning panel | Auto, 0–4 | Auto (§7.1) |
| Depth anti-aliasing | Tuning panel | Auto / On / Off | Auto: DA2 and DA3 on |
| Warp | Tuning panel | Splat / Lookup | Splat until Phase 3 is done |
| Edge snapping | Tuning panel | On / Off | Off for DA2 and DA3 |
| Screen-pixel dilation | Tuning panel | 0–8 px | 0 |
| Use page data | Settings | Auto / Off | Auto: used whenever the extension follows the video |

## Appendix: where each change lives

| Change | Files |
|---|---|
| Measures M1–M5 | `Session` (stats), `StereoSynth` (timestamps, hole counter), `DepthPostProcessor` (flicker), `AiStereo` (depth age) |
| Matched timing | `AiStereo` (mode, selection, λ blend), `VideoDelayBuffer` (frames with bracketing depth), `AppController` (sound rule), `StereoSettings` |
| Soft edges, anti-aliasing | `DepthPostProcessor` (or its GPU successor), `StereoTuning` |
| Lookup warp | `StereoSynth` (new pass, run maxima), `StereoTuning` |
| Depth path | `DepthService` (overlap, earlier hand-over), `DepthEstimator` (IOBinding), `StereoSynth` (GPU post-processing) |
| Look-ahead normalisation | `DepthPostProcessor` (relative depth and percentiles out), `AiStereo` (display-time normalisation) |
| Subtitle rule | `CaptionLock` |
| Extension protocol 2 | `extension/content.js` (frame callback, overlay probe, caption selectors), `extension/background.js` (relay, clock), `Browser/BrowserLink` and `Browser/BrowserMedia` (messages, clock), `AppController` (follow) |
| Hand-over gating | `AiStereo` (new-frame rule), `DuplicationCapture` (dirty rectangles) |
| Flat regions from the page | `DepthPostProcessor`, `CaptionLock` |
