# The browser extension

Things measured the hard way, for work in this folder (the rest are in the root `CLAUDE.md`).

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
