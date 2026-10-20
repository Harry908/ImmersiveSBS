# ImmersiveSBS link (browser extension)

Part of the ImmersiveSBS app, for Chrome and Edge. It is how browser video reaches the glasses in
3D:

- **The 3D theater.** While the glasses are in 3D mode, a "3D theater" button shows over a video
  under the mouse. It opens that video alone, full screen, in a player in the page, drawn sharp
  at the screen's resolution; the app shows it in 3D in the glasses. The theater is full screen
  for as long as it is open: leaving full screen (Esc, F11) leaves it, and the page goes back to
  its window.
- **Its controls** sit together toward the middle of the screen, away from the edges the glasses
  blur: one frosted bar with the seek bar, the time, 10 s back and forward, play/pause, mute,
  the 3D settings panel (model, resolution, depth, screen plane, timing, stretch, whether the
  video is 2D or already 3D) and Exit. The bar hides while the video plays and the mouse rests.
  A click on the picture plays or pauses. Keys: Space or K play/pause, ← → or J L skip 10 s,
  M mute, Esc leaves. The page's subtitles are shown in the picture, whether or not the site's
  own player was full screen.
- **Pause, play and seek without gaps.** With the buffer, a pause holds the delayed picture and
  sound, and play goes on from the same moment; opening the theater or seeking shows a spinner
  while the buffer fills (its length, 2 s by default). A seek while paused shows the new place
  at once. A live stream is never buffered.
- **The delay.** For the buffer and matched timing, the player shows the video late itself and
  plays that tab's sound as late, through Web Audio, so it keeps its full quality and its usual
  output; other tabs stay live. Its controls stay live on top, and the app shows the screen as
  it is. Meanwhile it sends the app each frame as it plays, small, so the depth is ready before
  the frame is shown.

The app speaks one version of it (protocol 3; this is extension 1.8.3). An older copy still
loaded in the browser is refused, and the app says to reload it.

## Install

The app's installer opens Edge's extensions page and the extension folder on its last page.
Otherwise:

1. Open `edge://extensions` (or `chrome://extensions`).
2. Turn on **Developer mode**.
3. Choose **Load unpacked** and pick the `extension` folder next to `ImmersiveSBS.exe`
   (Settings → **Open extension folder**).

After the app is updated, press the reload button on the extension's card, then reload the tabs
that play video (a tab opened before keeps the old script).

## Is it working?

Pin the extension to the toolbar (the puzzle-piece menu → the pin next to ImmersiveSBS link).
Its badge says:

- **off** (red): the app is not running, or not reachable.
- **on** (grey): connected to the app.
- **3D** (green), on one tab: that tab's video is in the 3D theater. Hover over the button to see
  how late the page plays its sound.

The app's log (`%LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log`) says the same in its `Browser:`
lines, and Settings shows the connection.

## How it works

- `content.js` runs in every frame. It reports the page's video and audio elements, plays their
  sound late when asked (element → `DelayNode` → output), shows the theater button and runs the
  player: a closed shadow root over the page that draws the video on a canvas (WebGL 2). While
  the sound is delayed the player keeps each frame in GPU memory (luma and half-size chroma, at
  the size shown) and draws it when its sound is heard; a pause holds both, a seek starts both
  again. While the player is open it reports where its picture is on the screen, each frame it
  draws (on the app's clock), play/pause and seeking, the boxes of its bar, its 3D panel, its
  spinner and its caption (which the app keeps flat), and each frame the video presents as a
  small JPEG at the depth model's input size. The page's captions (its caption boxes in the
  player around the video, and the video's own text tracks) are copied into the player, late
  with the frame they were read with.
- `background.js` connects to the app at `ws://127.0.0.1:47391/immersivesbs` and relays. When
  the app closes or the connection drops, every page goes back to live sound. When the app's tray
  opens the theater (no click in the page, which full screen needs), it picks the largest
  playing video of the tab in front and puts the browser window in full screen.

It talks only to the app on this PC. It sends no data anywhere else and stores nothing.

## What it sends the app

Only what places and times the picture, and nothing that says what you watch:

- The browser's name and the extension's version, once.
- For each video or audio element: a number for it, whether it is a video, playing and
  audible, its picture's size on the page, the video's width and height in px, whether its sound
  can be delayed (and if not, one word: protected, cross-origin, blocked or source), how late
  it plays now, and whether it is in the theater.
- For the theater's video: where the player shows it, each frame's count and time in the video,
  play/pause and seeking, the boxes of the player's controls and captions (position and size),
  a clock offset, which button of its 3D panel was pressed, and its frames, small (the depth
  model's input size, about 55 KB each), which the app turns into depth and does not keep or
  save; the app sees the same picture on the screen anyway.

Never page titles, web addresses, video sources or names, caption text, or anything typed. Some
of it is read inside the page (whether a video's source is from another site, the captions it
copies into the player), but it does not leave the page.

## Limits

- Protected (DRM) video, and media from another site served without CORS, cannot be routed
  through Web Audio (their sound would go silent) or read by the player. The extension leaves
  them alone: they play live, and the app takes the depth from the screen.
- A page where nothing has been clicked yet may not be allowed to start audio; its sound stays
  live until then.
- Captions that a site draws without caption-like boxes do not reach the player. Captions burned
  into the video are part of the picture, and the app keeps them flat.
- The extension's ID (`dopaoaefmhhmpbdgpnkgpkjhbndebmnl`) comes from the `key` in
  `manifest.json`. The app accepts connections only from that ID.
