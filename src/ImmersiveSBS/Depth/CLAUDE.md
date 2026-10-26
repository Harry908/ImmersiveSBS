# The depth path

Things measured the hard way, for work in this folder (the rest are in the root `CLAUDE.md`).

- The forward splat opens a hole wherever nearness changes by more than 2/S per px
  (improvement-plan.md §2.1); soft edges alone do not prevent it for big steps, the slope limit
  does. Holes at the region's side edges are not counted in M2.
- `DepthService.TryTake` swaps frames with the caller; frames from another model size must not
  be recycled.
- The first session from a newly built engine keeps the kernel compilation it did: 2.3 GB
  against 0.8 GB for Distill-Any-Depth Base 756×420 (6.9 GB for the app with Video Depth
  Anything's two graphs), until the model is unloaded. `DepthEstimator.EnsureEngine` makes and
  drops one session after each build, so the kept one loads from the runtime cache (1.1 GB).
  The first inference of a session takes 45–480 ms and the post-processing's first calls 48,
  10, 7 ms before 6: `DepthService` warms both before it says Ready. `EnginePreparer` builds
  every offered model's engines in the background after start (while 3D is off), so a change
  of model loads in about a second instead of 5–26 s.
- On 150 frames of the sample clip (`TemporalDepthTests`) Depth Anything 3 flickers 2.3–2.7
  times as much as Distill-Any-Depth Base (M4, alone and through the service); Video Depth
  Anything Small alone 36–45 % as much, but the service's per-pixel smoothing evens the two out
  on still pixels, costing lag where things move. Its streaming mode is the authors'
  training-free one: their offline mode (32-frame windows) scores better (ScanNet δ1 0.926
  against 0.836).
- Depth smoothed at its place (a pixel's own history) could not smooth a moving thing (its
  picture changes under every pixel, so each took the new depth: it flickered) and left a trail
  where the picture behind looked like it. Along the motion (`MotionField`, block matching on
  the model input's luma) and taking a depth jump at once where the picture moves within 4 px:
  on the sample clip, flicker along the motion on moving pixels 56–61 % lower for all three
  models, still pixels unchanged; on a synthetic moving object, its flicker as low as the still
  background's and the trail behind it 0.002 against 0.14.
- TensorRT for RTX runs a model at the precision of its file, so speed needs fp16 files.
  `onnxconverter_common`'s fp16 converter breaks on a dynamic export's shape arithmetic (casts
  around Shape/Gather/Range); fold it into constants first (ORT's basic optimizer on the static
  fp32 model), then convert. Cached engines are named by a fingerprint of the model file.
- Page reports (frames, boxes) go page → worker → socket and often arrive after the capture of
  the frame they describe. Matching a capture only with reports already in dropped 40 % of a
  30 fps video's frames; `PageVideo.TryFrameAt` counts frames on past the newest report (not
  for the player's own delayed frames: it holds one while a new delay fills, the video playing
  on), and boxes go straight to `PageVideo` with their time (never through the controller's
  250 ms timer) so the depth thread takes each frame's own.
- TensorRT for RTX fuses MatMul–softmax–MatMul into an attention kernel that gives NaN for heads
  of 8 and 24 channels (Video Depth Anything's 64- and 192-channel temporal modules; 48 works),
  also with the softmax written out. The export writes that attention without matrix products.
  A traced `torch.empty` (baddbmm with beta 0) becomes a constant of whatever memory held.
