# ImmersiveSBS link (browser extension)

Part of the ImmersiveSBS app, for Chrome and Edge. It is how browser video reaches the glasses in
3D:

- **The 3D theater.** While the glasses are in 3D mode, a "3D theater" button shows over a video
  under the mouse. It opens that video alone, full screen, in a player in the page, drawn at the
  video's own resolution; the app shows it in 3D in the glasses. The theater is full screen for
  as long as it is open: leaving full screen (Esc, F11) leaves it, and the page goes back to its
  window. One frosted bar at the bottom holds a
  seek bar, the time, 10 s back and forward, play/pause, mute, the 3D settings panel (model,
  resolution, depth, screen plane, timing, stretch, whether the video is 2D or already 3D, edge
  snapping, flat subtitles) and Exit; it hides while the video plays and the mouse rests. A click
  on the picture plays or pauses. Keys: Space or K play/pause, ← → or J L skip 10 s, M mute, Esc
  leaves. The page's subtitles are shown in the picture, whether or not the site's own player
  was full screen.
- **The delayed sound.** When the app shows the video late (matched timing or the buffer), the
  page plays that tab's sound as late, through Web Audio, so it keeps its full quality and its
  usual output; other tabs stay live. The app has no other way to delay sound.

The app speaks one version of it (protocol 3). An older copy still loaded in the browser is
refused, and the app says to reload it.

## Install

1. Open `chrome://extensions` (or `edge://extensions`).
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
  player: a closed shadow root over the page that draws the video on a canvas. While the player
  is open it reports where its picture is on the screen, each frame it draws (on the app's
  clock), play/pause and seeking, and the boxes of its bar, its 3D panel and its caption, which
  the app keeps flat. The app draws the bar and the panel from the live screen over the delayed
  3D picture; they are frosted, so the live video under them shows only as a blur. The page's
  captions (its caption boxes in the player around the video, and the video's own text tracks)
  are copied into the player.
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
  a clock offset, and which button of its 3D panel was pressed.

Never page titles, web addresses, video sources or names, caption text, or anything typed. Some
of it is read inside the page (whether a video's source is from another site, the captions it
copies into the player), but it does not leave the page.

## Limits

- Protected (DRM) video, and media from another site served without CORS, cannot be routed
  through Web Audio; their sound would go silent. The extension leaves them alone: their sound
  stays live, there is no buffer for them, and matched timing stays within lip sync.
- A page where nothing has been clicked yet may not be allowed to start audio; its sound stays
  live until then.
- Captions that a site draws without caption-like boxes do not reach the player. Captions burned
  into the video are part of the picture, and the app keeps them flat.
- The extension's ID (`dopaoaefmhhmpbdgpnkgpkjhbndebmnl`) comes from the `key` in
  `manifest.json`. The app accepts connections only from that ID.
