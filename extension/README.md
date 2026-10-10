# ImmersiveSBS link (browser extension)

Optional companion to the ImmersiveSBS app, for Chrome and Edge. When the app's buffer is on, it
plays a video's sound late inside the page, in step with the delayed 3D picture, so the sound keeps
its full quality and its usual output. It also tells the app exactly where the video is.

## Install

1. Open `chrome://extensions` (or `edge://extensions`).
2. Turn on **Developer mode**.
3. Choose **Load unpacked** and pick this folder. The app copies it next to `ImmersiveSBS.exe`;
   Settings → Buffer → **Open extension folder** opens that copy.

After the app is updated, press the reload button on the extension's card.

## What it does

- `content.js` runs in every frame. It reports the page's video and audio elements to the
  worker: playing or not, audible, where the picture is, and whether the sound can be delayed.
  When the app asks for a delay, it routes each playing element through Web Audio
  (element → `DelayNode` → output).
- `background.js` connects to the app at `ws://127.0.0.1:47391/immersivesbs` and relays. When
  the app closes or the connection drops, every page goes back to live sound.

It talks only to the app on this PC. It sends no data anywhere else and stores nothing.

## Limits

- Protected (DRM) video, and media from another site served without CORS, cannot be routed
  through Web Audio; their sound would go silent. The extension leaves them alone, and the app
  captures the browser's sound instead.
- A page where nothing has been clicked yet may not be allowed to start audio. The app falls
  back to capturing the sound there too.
- The extension's ID (`dopaoaefmhhmpbdgpnkgpkjhbndebmnl`) comes from the `key` in
  `manifest.json`. The app accepts connections only from that ID.
