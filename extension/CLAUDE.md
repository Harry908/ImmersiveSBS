# The browser extension

What the app expects of the page (the delay rule, the theater's reports, the boxes it draws
flat) is in the root `CLAUDE.md`; this file is the page's own side.

## The theater's player

- **Opening.** Pressed with a real click (so the page may go full screen), a player in a closed
  shadow root covers the page, goes full screen and draws the video on a WebGL 2 canvas at the
  picture's size on the screen (stretched if set; a 2D canvas at the video's size for a video
  it may not read). While the app says a model is loading, the player waits, paused
  (`holdForModel`).
- **Full screen.** It stays full screen while open: leaving full screen closes it (no windowed
  theater), and the page is back in its window. Opened while the page is in its own full
  screen, the player goes inside that element and uses it instead of going full screen over
  it: with two levels, YouTube missed the end of its own and stayed in its full-screen layout
  after the theater closed.
- **Controls.** One frosted bar (seek bar, time, ±10 s, play/pause, mute, 3D panel, exit; keys
  Space/K, ←/→ J/L, M, Esc; a click on the picture plays or pauses) and a 3D panel; the bar,
  the panel and the spinner stay live over the delayed picture.
- **Captions.** The page's captions are copied in: its caption boxes within 6 ancestors of the
  video, and text tracks, read with each frame shown. They are read only when a
  MutationObserver on the page's player or a `cuechange` says they changed, and once a second
  anyway: a read forces the page's layout.
- **The delayed picture.** Each presented frame is kept as luma and half-size chroma, 1.5 bytes
  a pixel at the size shown or the video's own if smaller; slots no longer needed are given
  back (`trimSlots`). Each is stamped on the audio clock and drawn when its sound is heard.
- **Pause, end, seek.** A pause suspends the AudioContext, which holds sound and picture; at
  the end the tail plays out; a seek starts the delay line again, drops the kept frames not
  shown and starts a new line; the player shows a spinner until the new place's first frame; a
  paused seek shows its frame at once.

## Things measured the hard way

- In an element's full screen Edge's `outerWidth`/`outerHeight` need not match `screen` (1489×832
  against 1501×844): the theater counts as on the screen when `document.fullscreenElement` is
  its player, not by comparing sizes. A hidden element's `innerText` is all its text, so caption
  boxes are read only when `checkVisibility()` says they are shown. While another element (the
  theater) is full screen, WeTV lays its video out at no height (937×0) but keeps its caption
  shown: captions are not tested against a video box that has no size.
- An element replaced between a press and its release gets no click: the player rewrote its
  play icon on every mouse move (`showControls`), so a press on the icon with the slightest move
  did nothing (6 of 6 in a test). Icons change only when they differ (`setIcon`), icons take no
  pointer events, and the 3D panel is built once and only its values change.
- Python's `http.server` answers no range requests: a page test's video served by it cannot
  seek (every seek lands at 0). Load it as a blob for seek tests.
- `createMediaElementSource` reroutes an element for good, and through a suspended
  `AudioContext` it goes silent: the extension attaches only playing elements, only once its
  context runs, and never cross-origin or protected media (Web Audio gets silence from them).
