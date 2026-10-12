# ImmersiveSBS link (browser extension)

Optional companion to the ImmersiveSBS app, for Chrome and Edge. When the app shows a video late
(matched timing or the buffer), it plays the sound of that video's tab late inside the page, in
step with the 3D picture, so the sound keeps its full quality and its usual output: the sound
still comes from the browser, not from the app, and other tabs stay live. The app has no other
way to delay sound: without the extension there is no buffer, and matched timing stays within
90 ms with the sound live. It also tells the app exactly where the video is, each frame it
shows, pause and seeks, and the controls and captions drawn over it.

## Install

1. Open `chrome://extensions` (or `edge://extensions`).
2. Turn on **Developer mode**.
3. Choose **Load unpacked** and pick this folder. The app copies it next to `ImmersiveSBS.exe`;
   Settings → Buffer → **Open extension folder** opens that copy.

After the app is updated, press the reload button on the extension's card, then reload the tabs
that play video (a tab opened before keeps the old script).

## Is it working?

Pin the extension to the toolbar (the puzzle-piece menu → the pin next to ImmersiveSBS link).
Its badge says:

- **off** (red): the app is not running, or not reachable.
- **on** (grey): connected to the app.
- **3D** (green), on one tab: the app follows the video in that tab. Hover over the button to see
  how late the page plays its sound.

The app's log (`%LOCALAPPDATA%\ImmersiveSBS\ImmersiveSBS.log`) says the same in its `Browser:`
lines, and Settings shows the connection.

## What it does

- `content.js` runs in every frame. It reports the page's video and audio elements to the
  worker: playing or not, audible, where the picture is, and whether the sound can be delayed.
  When the app asks for a delay, it routes each playing element through Web Audio
  (element → `DelayNode` → output). In the app's 3D theater it plays, pauses and seeks the
  followed video on the app's request (the player window has the keyboard, not the page).
- `background.js` connects to the app at `ws://127.0.0.1:47391/immersivesbs` and relays. When
  the app closes or the connection drops, every page goes back to live sound.

It talks only to the app on this PC. It sends no data anywhere else and stores nothing.

## What it sends the app

Only what places and times the picture, and nothing that says what you watch:

- The browser's name and version, once.
- For each video or audio element: a number for it, whether it is a video, playing, audible
  and visible, how often it seeked, where its picture is in the tab (in px), the tab's size and
  zoom, the video's width and height in px, whether its sound can be delayed (and if not, one
  word: protected, cross-origin, blocked or source) and how late it plays now.
- For the one video the app follows: each frame's count and time in the video, play/pause and
  seeking, where it is, how long it is and whether it is muted (for the theater's controls),
  the boxes drawn over it (position, size, and whether each is a caption, a control or
  other), and a clock offset.

Never page titles, web addresses, video sources or names, caption text, or anything typed. To
decide those things it reads some of them inside the page (whether a video's source is from
another site, whether a caption box has text, how many lines a caption has), but they do not
leave the page.

## Limits

- Protected (DRM) video, and media from another site served without CORS, cannot be routed
  through Web Audio; their sound would go silent. The extension leaves them alone, and the app
  captures the browser's sound instead.
- A page where nothing has been clicked yet may not be allowed to start audio. The app falls
  back to capturing the sound there too.
- The extension's ID (`dopaoaefmhhmpbdgpnkgpkjhbndebmnl`) comes from the `key` in
  `manifest.json`. The app accepts connections only from that ID.
