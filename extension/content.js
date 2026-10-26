// ImmersiveSBS link: the page script, in every frame. It is part of the ImmersiveSBS app: the app
// finds browser video only through it, and shows browser video in 3D only in its theater.
//
// - It reports the page's video and audio elements (playing, audible, whether their sound can be
//   delayed) and, when the app asks, plays their sound late through Web Audio: element ->
//   DelayNode -> output. The sound never leaves the browser.
// - While the app says the glasses are in 3D mode, it shows a "3D theater" button over a video
//   under the mouse. The theater is that video alone, full screen for as long as it is open: a
//   player covering the page draws it on a canvas, with the page's captions and one control bar.
//   While the sound is delayed, the player shows the picture as late itself, its controls live
//   over it: the app shows that screen as it is and turns the picture into 3D.
// - While the theater is open it reports, for that video only: where the picture is on the
//   screen, each frame it draws (on the app's clock), its playback state, the boxes of its bar,
//   its 3D panel and its caption (which the app keeps flat), and each frame the video presents,
//   small (the depth model's input size, JPEG), as it plays: the app's model gets the depth
//   ready before the player shows it. The frames go only to the app on this PC.
//
// What cannot be delayed is left alone and reported: protected (DRM) media, media from another
// origin without CORS (Web Audio would get silence from it), and pages where the browser does
// not let audio start before a click.
'use strict';

(() => {
  const PORT_NAME = 'immersivesbs-frame';
  const REPORT_MS = 500;
  const SHADOW_SCAN_MS = 5000;       // while the app is connected
  const IDLE_SHADOW_SCAN_MS = 30000; // while it is not (a page whose only video is in a shadow root)
  const CLOCK_MS = 5000;             // clock re-sync while the theater is open
  const CLOCK_SAMPLES = 8;           // the best of this many round trips counts
  // Caption containers of common players, whose text the theater shows; native <track> captions
  // come from textTracks.
  const CAPTION_SELECTORS = [
    '.ytp-caption-window-container', '.caption-window', '.ytp-caption-segment',   // YouTube
    '.vjs-text-track-display', '.vjs-text-track-cue',                            // Video.js
    '.plyr__captions', '.plyr__caption',                                         // Plyr
    '.jw-captions', '.jw-text-track-container',                                  // JW Player
    '.vp-captions',                                                               // Vimeo
    '[data-a-target="player-captions-container"]', '.player-captions-container',  // Twitch
    '.text-track',                                                                // WeTV
    '[class*="caption" i]', '[class*="subtitle" i]',
  ].join(',');
  // What a caption box never is or holds: controls, menus, lists, dialogs.
  const CAPTION_NOT = 'button, select, input, label, ul, ol, li, [role="button"], [role="menu"], [role="menuitem"], ' +
    '[role="menuitemradio"], [role="listbox"], [role="option"], [role="dialog"], [role="radiogroup"], [role="radio"]';
  const CAPTION_NOT_CLASS = /menu|setting|select|option|switch|btn|button|popup|panel|list|toggle|icon|tips?\b/i;
  const CAPTION_MAX_LINES = 4;

  /** element -> { id, delay node, delay applied (s) } for elements in the page. */
  const media = new Map();
  /** element -> MediaElementAudioSourceNode: made once per element, ever (a second throws). */
  const sources = new WeakMap();
  let nextId = 1;
  let port = null;
  let appConnected = false; // the worker has the app: only then is anything measured and sent
  let wanted = 0;           // delay asked for, s
  let wantedPicture = 0;    // how late the theater shows the picture, s (the sound's, less the trim and output latency)
  let context = null;       // AudioContext, made on the first delay
  let holding = false;      // the context is suspended by us: the theater's video is paused
  let contextRan = false;   // the browser has let the context run once: audio is allowed in this page
  let applying = Promise.resolve();
  let lastSent = '';
  let lastShadowScan = 0;
  const listened = new WeakSet();
  let clockSamples = [];
  let theta = NaN;          // app clock − page clock, ms
  let clockError = NaN;     // at most this far off, ms

  // ---- Finding media ---------------------------------------------------------------------

  function scan() {
    for (const el of document.getElementsByTagName('video')) track(el);
    for (const el of document.getElementsByTagName('audio')) track(el);
    // Players that keep the element inside an open shadow root (their events do not reach the
    // document): look there now and then, in a page that is shown (a walk of every element).
    const now = performance.now();
    const every = appConnected ? SHADOW_SCAN_MS : IDLE_SHADOW_SCAN_MS;
    if (now - lastShadowScan > every && document.visibilityState === 'visible') {
      lastShadowScan = now;
      for (const host of document.querySelectorAll('*')) if (host.shadowRoot) scanShadow(host.shadowRoot, 0);
    }
    // Elements gone from the page are forgotten once they are silent, played live again first:
    // an element taken into Web Audio stays there, and could come back.
    for (const [el, state] of media) {
      if (el.isConnected || !el.paused) continue;
      route(el, state, null);
      media.delete(el);
    }
  }

  function scanShadow(root, depth) {
    for (const el of root.querySelectorAll('video, audio')) track(el);
    if (depth < 4) for (const host of root.querySelectorAll('*')) if (host.shadowRoot) scanShadow(host.shadowRoot, depth + 1);
  }

  function track(el) {
    if (media.has(el)) return;
    media.set(el, { id: nextId++, node: null, applied: 0 });
    connectPort();
    if (listened.has(el)) return;
    listened.add(el);
    for (const type of ['play', 'playing', 'pause', 'ended', 'seeking', 'seeked', 'ratechange', 'volumechange', 'loadedmetadata', 'emptied']) {
      el.addEventListener(type, () => {
        const state = media.get(el);
        if (!state) return;
        if (type === 'seeked') state.seeks = (state.seeks ?? 0) + 1;
        if (type === 'playing') queueApply();
        if (theater?.el === el) {
          onTheaterMedia(el, state, type);
          sendState(false);
        }
        sendSoon();
      });
    }
  }

  // Media events do not bubble, but a capture listener on the document sees them in the light DOM.
  document.addEventListener('play', e => {
    if (e.target instanceof HTMLMediaElement) {
      track(e.target);
      queueApply();
    }
  }, true);

  // ---- Describing media ------------------------------------------------------------------

  /** Why the element's sound cannot be delayed in the page, or null. */
  function blocker(el) {
    if (el.mediaKeys) return 'protected';
    if (el.srcObject) return null;
    const src = el.currentSrc || el.src;
    if (!src) return null;
    let url;
    try {
      url = new URL(src, location.href);
    } catch {
      return 'source';
    }
    if (url.protocol === 'blob:' || url.protocol === 'data:') return null;   // media source, made by the page
    if (url.origin === location.origin || el.crossOrigin !== null) return null;
    return 'cross-origin';
  }

  function hasAudio(el) {
    const decoded = el.webkitAudioDecodedByteCount;
    return decoded === undefined || decoded > 0;
  }

  /**
   * The video picture inside the element's box (object-fit), in this frame's viewport CSS px, or
   * null; in the theater, the player's canvas.
   */
  function pictureRect(el) {
    if (el.tagName !== 'VIDEO') return null;
    if (theater?.el === el) return theaterPicture(theater);
    const box = el.getBoundingClientRect();
    if (box.width < 2 || box.height < 2) return null;
    const style = getComputedStyle(el);
    const left = box.left + el.clientLeft + parseFloat(style.paddingLeft);
    const top = box.top + el.clientTop + parseFloat(style.paddingTop);
    const width = el.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    const height = el.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
    const vw = el.videoWidth, vh = el.videoHeight;
    const fit = style.objectFit || 'contain';
    if (vw > 0 && vh > 0 && (fit === 'contain' || fit === 'scale-down' || fit === 'none')) {
      // The default for video: the picture letterboxed in the box ("none": at its own size).
      const contain = Math.min(width / vw, height / vh);
      return centred(left, top, width, height, vw, vh, fit === 'contain' ? contain : fit === 'none' ? 1 : Math.min(contain, 1));
    }
    return { x: left, y: top, width, height };
  }

  /** A <vw>×<vh> picture at <scale>, centred in the box at <x>, <y> of <width>×<height>. */
  function centred(x, y, width, height, vw, vh, scale) {
    const w = vw * scale, h = vh * scale;
    return { x: x + (width - w) / 2, y: y + (height - h) / 2, width: w, height: h };
  }

  /** What the app needs to know about an element: for the sound, and which video the theater shows. */
  function describe(el, state) {
    // A context the browser has not let run yet (no click in the page) blocks the delay. One that
    // ran once and is suspended now is ours: held for a pause, or resuming from one, which takes
    // a moment; reported as blocked then, the app dropped the buffer on every play.
    const reason = blocker(el) ?? (wanted > 0 && context && context.state !== 'running' && !holding && !contextRan ? 'blocked' : null);
    const r = document.visibilityState === 'visible' ? pictureRect(el) : null;
    return {
      id: state.id,
      kind: el.tagName === 'VIDEO' ? 'video' : 'audio',
      playing: !el.paused && !el.ended,
      audible: !el.muted && el.volume > 0 && hasAudio(el),
      // The picture's size on the page, CSS px², 0 when not shown: the tray's theater opens on
      // the largest playing video of the tab in front.
      area: r ? Math.round(r.width * r.height) : 0,
      videoWidth: el.videoWidth || 0,
      videoHeight: el.videoHeight || 0,
      delayable: reason === null,
      reason,
      delay: state.applied,
      latency: context ? Math.round(((context.baseLatency || 0) + (context.outputLatency || 0)) * 1000) : null,
      theater: theater?.el === el,
    };
  }

  // ---- Talking to the worker -------------------------------------------------------------

  function connectPort() {
    if (port || media.size === 0 || !chrome.runtime?.id) return;
    try {
      port = chrome.runtime.connect({ name: PORT_NAME });
    } catch {
      return; // the extension was reloaded or removed: this script is an orphan now
    }
    port.onMessage.addListener(onWorkerMessage);
    port.onDisconnect.addListener(() => {
      port = null;
      lastSent = '';
      // No app behind the worker any more (or no worker): nothing stays late.
      appConnected = false;
      wanted = 0;
      queueApply();
      setTimeout(connectPort, 3000);
    });
    lastSent = '';
    send();
  }

  function onWorkerMessage(m) {
    if (m?.type === 'delay') {
      wanted = Number(m.seconds) > 0 ? Number(m.seconds) : 0;
      wantedPicture = wanted > 0 && Number(m.picture) > 0 ? Number(m.picture) : 0;
      queueApply();
    } else if (m?.type === 'app') {
      appConnected = m.connected === true;
      if (!appConnected) showButton(null);
      lastSent = '';
      send();
      if (theater) reportTheater(theater, true);
    } else if (m?.type === 'status') {
      const size = m.picture;
      appStatus = {
        glasses: m.glasses === true, settings: m.settings ?? null,
        picture: Number(size?.width) > 0 && Number(size?.height) > 0 ? { width: Number(size.width), height: Number(size.height) } : null,
        loading: m.loading === true,
      };
      if (!appStatus.glasses) showButton(null);
      renderPanel();
      if (theater) {
        holdForModel(theater);
        // The stretch may have changed.
        if (delaying(theater)) sizePicture(theater);
        else drawLive(theater, null);
        sendState(true);
      }
    } else if (m?.type === 'theater') {
      // The app's tray or shortcut: open the player on one video (the worker chose it), or close it.
      if (m.open !== true) closeTheater();
      else for (const [el, state] of media) if (state.id === m.id) openTheater(el, false);
    } else if (m?.type === 'clock') {
      onClock(m);
    }
  }

  let sendTimer = 0;
  function sendSoon() {
    if (!sendTimer) sendTimer = setTimeout(send, 50);
  }

  function send() {
    clearTimeout(sendTimer);
    sendTimer = 0;
    if (!port || !appConnected) return;
    const list = [];
    for (const [el, state] of media) list.push(describe(el, state));
    const text = JSON.stringify(list);
    if (text === lastSent) return;
    lastSent = text;
    post({ type: 'media', media: list });
  }

  function post(message) {
    if (!port || !appConnected) return;
    try {
      port.postMessage(message);
    } catch {
      port = null;
    }
  }

  // ---- Delaying the sound ----------------------------------------------------------------

  function queueApply() {
    applying = applying.then(applyAll, applyAll);
  }

  async function applyAll() {
    for (const [el, state] of media) {
      try {
        await apply(el, state);
      } catch (e) {
        console.debug('ImmersiveSBS link:', e);
      }
    }
    send();
  }

  /** The browser lets audio start only after the page had a click; resolves whether it runs (or is held by us). */
  async function contextRunning() {
    if (!context) {
      context = new AudioContext({ latencyHint: 'interactive' });
      context.onstatechange = () => {
        if (context.state === 'running') contextRan = true;
        sendSoon();
      };
    }
    if (holding) return true;
    if (context.state !== 'running') {
      try {
        await Promise.race([context.resume(), new Promise(r => setTimeout(r, 500))]);
      } catch {
        // stays suspended
      }
    }
    return context.state === 'running';
  }

  async function apply(el, state) {
    const want = wanted;
    if (want > 0) {
      if (blocker(el) !== null) return;
      const source = sources.get(el);
      // An element is taken into Web Audio once it plays; a paused one waits.
      if (!source && el.paused) return;
      // Routed through a context that is not running, the element would go silent.
      if (!(await contextRunning())) return;
      if (state.applied === want && state.node) return;
      let src = source;
      if (!src) {
        src = context.createMediaElementSource(el);
        sources.set(el, src);
        src.connect(context.destination);
      }
      if (state.node && Math.abs(want - state.applied) <= 0.3 && want <= state.node.maxDelay) {
        // A trim: glide there (a moment's pitch bend) rather than starting the line again.
        state.node.delayTime.setTargetAtTime(want, context.currentTime, 0.05);
      } else {
        // A fresh line is silent for the delay, as the picture holds its first frame.
        newLine(el, state, Math.min(179, want + 1), want);
      }
      state.applied = want;
    } else if (state.applied !== 0 || state.node) {
      route(el, state, null);
    }
  }

  /** A new delay line for <el>, silent for <seconds>; the theater's spinner shows while it fills. */
  function newLine(el, state, maxDelay, seconds) {
    const node = new DelayNode(context, { maxDelayTime: maxDelay, delayTime: seconds });
    node.maxDelay = maxDelay;
    route(el, state, node);
    if (theater?.el === el) filling(theater, seconds);
  }

  /** Sends an element taken into Web Audio through <node> (a DelayNode), or straight out (null). */
  function route(el, state, node) {
    const src = sources.get(el);
    if (!src) return;
    src.disconnect();
    if (node) src.connect(node).connect(context.destination);
    else src.connect(context.destination);
    if (state.node && state.node !== node) state.node.disconnect();
    state.node = node;
    if (!node) state.applied = 0;
  }

  // ---- Holding the delayed sound -----------------------------------------------------------
  // While the theater's video is paused its delayed sound is held, not drained: the audio
  // context is suspended, so the delay line keeps the seconds not yet heard and plays them when
  // the video plays again, without a gap; the picture, kept on that clock, holds with it. At the
  // end the delayed tail plays out. A network stall is not held: the buffered seconds play on,
  // and the stall comes that much later, sound and picture together, as it happened. A seek
  // starts the line again: the old position's sound and frames are dropped, and the theater
  // shows a spinner until the new position's are heard.

  function onTheaterMedia(el, state, type) {
    const t = theater;
    if (type === 'pause' && !el.ended) holdSound(state.node !== null);
    else if (type === 'playing' || type === 'ended') {
      holdSound(false);
      t.preview = false;
    } else if (type === 'seeking') {
      // A jump in the video: its times start a new line for the depth the app files by them.
      t.line++;
      // A looping video's jump back to the start plays on through the delay.
      if (el.loop && el.currentTime < 0.5) return;
      if (restartDelay(el, state)) {
        dropPictures(t);
        // The spinner stays until the new place's first frame is drawn (finding it takes a moment).
        t.waitFor = t.line;
        // Paused, the new place shows at once (it is heard as late, after play).
        t.preview = holding;
      }
    }
  }

  function holdSound(on) {
    if (!context || holding === on) return;
    holding = on;
    (on ? context.suspend() : context.resume()).catch(() => {});
  }

  /** A seek: the delay line starts again, silent for its delay. False when there is none. */
  function restartDelay(el, state) {
    if (!context || !state.node || state.applied <= 0) return false;
    newLine(el, state, state.node.maxDelay, state.applied);
    return true;
  }

  /** The theater's spinner shows while a new delay line is silent (half a second or more). */
  function filling(t, seconds) {
    if (seconds >= 0.5) t.fillUntil = context.currentTime + seconds;
  }

  /** How far behind the video what the viewer hears (and sees) is, s. */
  function behind(t) {
    const state = media.get(t.el);
    if (!context || !state?.node) return 0;
    return Math.max(0, state.applied - Math.max(0, t.fillUntil - context.currentTime));
  }

  // ---- The app's clock -------------------------------------------------------------------
  // NTP-style: θ = ((t1 − t0) + (t2 − t3)) / 2 is off by at most δ/2, δ = (t3 − t0) − (t2 − t1);
  // the sample with the smallest round trip of the last few counts. Frame times go to the app
  // on its clock.

  function pingClock() {
    if (theater) post({ type: 'clock', t0: performance.now() });
  }

  function onClock(m) {
    const t3 = performance.now();
    const t0 = Number(m.t0), t1 = Number(m.t1), t2 = Number(m.t2);
    if (![t0, t1, t2].every(Number.isFinite)) return;
    const delta = (t3 - t0) - (t2 - t1);
    if (delta < 0) return;
    clockSamples.push({ theta: ((t1 - t0) + (t2 - t3)) / 2, delta });
    if (clockSamples.length > CLOCK_SAMPLES) clockSamples.shift();
    const best = clockSamples.reduce((a, b) => (b.delta < a.delta ? b : a));
    theta = best.theta;
    clockError = best.delta / 2;
  }

  // ---- The 3D theater --------------------------------------------------------------------
  // While the app is connected and the glasses are in 3D mode, a "3D theater" button shows over a
  // video the mouse is on. The theater is that video alone, full screen: a player covering the
  // page draws it at its own size on a canvas (the capture gets it sharp, not as small as the
  // page laid it out), with the page's captions inside the picture and one bar of controls. It is
  // full screen for as long as it is open, so the glasses never show the page or the desktop
  // around the video: leaving full screen (Esc, F11) leaves the theater.
  //
  // While the sound is delayed (the buffer, matched timing) the player shows the picture as late
  // (see "The picture, late with the sound"), with the caption that went with each frame, and its
  // bar, 3D panel and spinner live over it: the screen is always the finished picture. Those are
  // reported as one box each, which the app draws flat in both eyes.

  const THEATER_MIN_WIDTH = 160, THEATER_MIN_HEIGHT = 90;
  const CONTROLS_MS = 2500;      // the bar hides this long after the mouse stops (while playing)
  const SEEK_STEP = 10;          // s, the ← → keys and the back / on buttons
  const CAPTION_ANCESTORS = 6;   // how far up from the video the page's player is looked for
  /** What the app tells: { glasses: in 3D mode (the button shows only then), settings: the 3D panel's values }. */
  let appStatus = { glasses: false, settings: null };
  let button = null;             // { host, el (the video under it), timer }
  let theater = null;            // the open player, see openTheater
  let moveQueued = false;

  const ICONS = {
    theater: 'M4 6h16v12H4zM2 4v16h20V4zm5 5h3v2H8v2h2v2H7zm6 0h2.5a2.5 2.5 0 0 1 0 6H13zm2 2v2h.5a1 1 0 0 0 0-2z',
    play: 'M8 5v14l11-7z',
    pause: 'M6 5h4v14H6zm8 0h4v14h-4z',
    back: 'M12 5V1L7 6l5 5V7a6 6 0 1 1-6 6H4a8 8 0 1 0 8-8z',
    forward: 'M12 5V1l5 5-5 5V7a6 6 0 1 0 6 6h2a8 8 0 1 1-8-8z',
    volume: 'M3 9v6h4l5 5V4L7 9zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z',
    muted: 'M16.5 12A4.5 4.5 0 0 0 14 8v2.2l2.45 2.45q.05-.3.05-.65M19 12q0 1.4-.55 2.65l1.5 1.5A9 9 0 0 0 14 3.2v2.1a7 7 0 0 1 5 6.7M4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25A7 7 0 0 1 14 18.7v2.1a9 9 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9zM12 4 9.91 6.09 12 8.18z',
    settings: 'M19.4 13a7.5 7.5 0 0 0 0-2l2.1-1.6-2-3.5-2.5 1a7.4 7.4 0 0 0-1.7-1L15 3.3h-4l-.4 2.6a7.4 7.4 0 0 0-1.7 1l-2.5-1-2 3.5L6.6 11a7.5 7.5 0 0 0 0 2l-2.1 1.6 2 3.5 2.5-1a7.4 7.4 0 0 0 1.7 1l.4 2.6h4l.4-2.6a7.4 7.4 0 0 0 1.7-1l2.5 1 2-3.5zM13 15.5a3.5 3.5 0 1 1 0-7 3.5 3.5 0 0 1 0 7z',
    close: 'M19 6.4 17.6 5 12 10.6 6.4 5 5 6.4l5.6 5.6L5 17.6 6.4 19l5.6-5.6 5.6 5.6 1.4-1.4-5.6-5.6z',
  };
  const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${ICONS[name]}"/></svg>`;

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; font-family: "Segoe UI", system-ui, sans-serif; }
    [hidden] { display: none !important; }
    /* Clicks land on the button, never on its icon: an icon swapped between press and release lost the click. */
    svg { width: 24px; height: 24px; fill: currentColor; display: block; pointer-events: none; }
    button { all: unset; cursor: pointer; color: #fff; display: inline-flex; align-items: center; justify-content: center;
      gap: 8px; min-width: 44px; height: 44px; padding: 0 12px; border-radius: 22px; background: rgba(28,28,32,.55);
      border: 1px solid rgba(255,255,255,.22); font-size: 15px; white-space: nowrap;
      backdrop-filter: blur(12px); -webkit-backdrop-filter: blur(12px); }
    button:hover, button:focus-visible { background: rgba(90,90,100,.6); border-color: #60cdff; }
    button.round { width: 48px; min-width: 48px; padding: 0; }
    button.big { width: 60px; height: 60px; border-radius: 30px; }
    button.big svg { width: 32px; height: 32px; }
  `;

  // -- The button over a video

  function onPointerMove(e) {
    if (moveQueued) return;
    moveQueued = true;
    const x = e.clientX, y = e.clientY, path = e.composedPath();
    requestAnimationFrame(() => {
      moveQueued = false;
      if (button && path.includes(button.host)) return showButton(button.el);   // on the button itself
      showButton(theater ? null : videoAt(x, y));
    });
  }

  /** The tracked video whose picture is at a point of this frame's viewport, big enough for the theater. */
  function videoAt(x, y) {
    if (!appConnected || !appStatus.glasses) return null;
    for (const [el] of media) {
      if (el.tagName !== 'VIDEO' || !el.isConnected) continue;
      const r = pictureRect(el);
      if (!r || r.width < THEATER_MIN_WIDTH || r.height < THEATER_MIN_HEIGHT) continue;
      if (x >= r.x && x < r.x + r.width && y >= r.y && y < r.y + r.height) return el;
    }
    return null;
  }

  function showButton(el) {
    if (!el) {
      if (button) button.host.style.display = 'none';
      return;
    }
    if (!button) {
      const host = document.createElement('div');
      const root = host.attachShadow({ mode: 'closed' });
      root.innerHTML = `<style>${STYLE}</style><button title="Open in the ImmersiveSBS 3D theater">${icon('theater')}3D theater</button>`;
      const b = root.querySelector('button');
      // The page must not see these: a click would also pause or open its video.
      for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'dblclick'])
        b.addEventListener(type, ev => ev.stopPropagation());
      b.addEventListener('click', ev => {
        ev.preventDefault();
        ev.stopPropagation();
        const target = button.el;
        showButton(null);
        if (target) openTheater(target, true);
      });
      button = { host, el: null, timer: 0 };
    }
    const r = pictureRect(el);
    if (!r) return showButton(null);
    const host = button.host;
    // In a page that is full screen only what is inside its full-screen element shows.
    const parent = document.fullscreenElement ?? document.documentElement;
    if (host.parentNode !== parent) parent.appendChild(host);
    button.el = el;
    host.style.cssText = `all: initial; position: fixed; z-index: 2147483647; display: block; ` +
      `left: ${Math.round(r.x + r.width / 2)}px; top: ${Math.round(r.y + 12)}px; transform: translateX(-50%);`;
    clearTimeout(button.timer);
    button.timer = setTimeout(() => showButton(null), CONTROLS_MS);
  }

  document.addEventListener('pointermove', onPointerMove, { capture: true, passive: true });
  window.addEventListener('scroll', () => showButton(null), { capture: true, passive: true });

  // -- The player

  const MEDIA_EVENTS = ['play', 'pause', 'seeked', 'volumechange', 'loadeddata', 'durationchange', 'ended'];

  /**
   * Opens the player on <el>, full screen. <gesture>: called from a click in the page, which lets
   * the player go full screen itself; asked by the app instead (tray, shortcut), the worker puts
   * the browser window in full screen.
   */
  function openTheater(el, gesture) {
    if (theater) closeTheater();
    if (!el?.isConnected || !media.has(el)) return;
    const host = document.createElement('div');
    host.style.cssText = 'all: initial; position: fixed; inset: 0; z-index: 2147483647; background: #000; display: block;';
    host.tabIndex = -1;
    const root = host.attachShadow({ mode: 'closed' });
    root.innerHTML = `<style>${STYLE}
      .stage { position: absolute; inset: 0; background: #000; }
      .stage.idle { cursor: none; }
      canvas { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }
      .caption { position: absolute; text-align: center; pointer-events: none; }
      .caption span { display: inline; white-space: pre-line; color: #fff; background: rgba(8,8,8,.75); padding: 2px 8px;
        font-size: inherit; line-height: 1.35; box-decoration-break: clone; -webkit-box-decoration-break: clone; }
      .frosted { background: rgba(20,20,24,.4); border: 1px solid rgba(255,255,255,.11);
        backdrop-filter: blur(28px) saturate(1.3); -webkit-backdrop-filter: blur(28px) saturate(1.3); }
      .frosted button { background: rgba(255,255,255,.064); backdrop-filter: none; -webkit-backdrop-filter: none; }
      .frosted button:hover, .frosted button:focus-visible { background: rgba(255,255,255,.16); }
      /* Kept toward the middle: the glasses blur the edges of the picture. */
      .bar { position: absolute; left: max(18%, calc(50% - 560px)); right: max(18%, calc(50% - 560px)); bottom: 6%;
        padding: 8px 18px 12px; border-radius: 12px; }
      .seek { position: relative; height: 24px; cursor: pointer; touch-action: none; }
      .track { position: absolute; left: 0; right: 0; top: 9px; height: 6px; border-radius: 3px; background: rgba(255,255,255,.3); }
      .seek:hover .track { top: 7px; height: 10px; border-radius: 5px; }
      .done { position: absolute; left: 0; top: 0; bottom: 0; border-radius: inherit; background: #60cdff; }
      .knob { position: absolute; top: 50%; width: 18px; height: 18px; margin: -9px 0 0 -9px; border-radius: 50%; background: #fff; }
      .row { display: grid; grid-template-columns: 1fr auto 1fr; align-items: center; margin-top: 6px; }
      .time { color: #fff; font-size: 18px; padding-left: 12px; font-variant-numeric: tabular-nums; }
      .middle, .right { display: flex; gap: 14px; align-items: center; }
      .right { justify-self: end; }
      .panel { position: absolute; left: 50%; transform: translateX(-50%); padding: 12px; border-radius: 12px; display: grid; gap: 8px; }
      .spinner { position: absolute; left: 50%; top: 50%; width: 88px; height: 88px; margin: -44px 0 0 -44px;
        border-radius: 50%; display: flex; align-items: center; justify-content: center; }
      .spinner .ring { width: 48px; height: 48px; border-radius: 50%; border: 5px solid rgba(255,255,255,.25);
        border-top-color: #fff; animation: spin .9s linear infinite; }
      @keyframes spin { to { transform: rotate(360deg); } }
      .panel .line { display: grid; grid-template-columns: 48px 340px 48px; gap: 8px; align-items: center; }
      .panel .value { color: #fff; font-size: 15px; padding: 0 12px; }
      .panel button.wide { grid-column: 1 / 4; justify-content: flex-start; }
      </style>
      <div class="stage"><canvas></canvas><div class="caption"></div></div>
      <div class="spinner frosted" hidden><div class="ring"></div></div>
      <div class="panel frosted" hidden></div>
      <div class="bar frosted">
        <div class="seek"><div class="track"><div class="done"></div></div><div class="knob"></div></div>
        <div class="row">
          <div class="time"></div>
          <div class="middle">
            <button class="round" data-act="back" title="10 s back (←)">${icon('back')}</button>
            <button class="round big" data-act="play" title="Play / pause (Space)"></button>
            <button class="round" data-act="forward" title="10 s on (→)">${icon('forward')}</button>
          </div>
          <div class="right">
            <button class="round" data-act="mute" title="Mute (M)"></button>
            <button data-act="settings" title="3D settings">${icon('settings')}3D</button>
            <button data-act="exit" title="Leave the theater (Esc)">${icon('close')}Exit</button>
          </div>
        </div>
      </div>`;
    const $ = s => root.querySelector(s);
    const t = {
      el, id: media.get(el).id, host, root, canvas: $('canvas'), stage: $('.stage'), caption: $('.caption'), panel: $('.panel'),
      bar: $('.bar'), seek: $('.seek'), done: $('.done'), knob: $('.knob'), time: $('.time'),
      play: $('[data-act="play"]'), mute: $('[data-act="mute"]'),
      ctx: null, pic: null, vfc: 0, raf: 0, timer: 0, clock: 0, ticks: 0, lastFrame: 0, lastMove: performance.now(), dragging: null,
      full: null, windowFull: false, wasScreen: false, controlsShown: true,
      fillUntil: 0, line: 0, waitFor: null, preview: false, spinner: $('.spinner'),
      captions: [], captionPlayer: null, captionObserver: null, captionsDirty: true, captionsStale: true, tracks: [],
      onCaptionChange: null, onTracks: null, liveCaption: '', shownCaption: '', captionStyle: '',
      liveTime: NaN, drawn: 0, drawnTime: NaN, drawnLine: 0, reported: '', boxes: '', state: '', onMedia: null, onResize: null,
      panelBuilt: false, modelHold: null,
      small: null, smallCtx: null, picturesOut: 0, picturesBlocked: false,
    };
    // Kept frames need WebGL, which refuses a video the page may not read (another site's, without CORS).
    t.pic = blocker(el) === 'cross-origin' ? null : createPicture(t.canvas);
    if (!t.pic) t.ctx = t.canvas.getContext('2d', { alpha: false });
    theater = t;

    root.addEventListener('click', e => {
      const target = e.target.closest?.('[data-act]');
      if (target) theaterAction(target.dataset.act, target);
      else if (t.stage.contains(e.target)) theaterAction('play');   // a click on the picture
    });
    host.addEventListener('pointermove', wake, { passive: true });
    // The page under the player must not see the mouse or scroll.
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click', 'dblclick', 'wheel', 'contextmenu'])
      host.addEventListener(type, e => e.stopPropagation());
    host.addEventListener('wheel', e => e.preventDefault(), { passive: false });
    t.seek.addEventListener('pointerdown', e => {
      t.seek.setPointerCapture(e.pointerId);
      t.dragging = seekFraction(e);
      showProgress();
    });
    t.seek.addEventListener('pointermove', e => {
      if (t.dragging === null) return;
      t.dragging = seekFraction(e);
      showProgress();
    });
    t.seek.addEventListener('pointerup', () => {
      if (t.dragging !== null && Number.isFinite(el.duration)) el.currentTime = t.dragging * el.duration;
      t.dragging = null;
    });
    t.onMedia = () => {
      // Paused, a seek shows its frame (with the picture delayed, only a seek's: see onTheaterMedia).
      if (el.paused && (!delaying(t) || t.preview)) onVideoFrame(t, null);
      showControls();
    };
    for (const type of MEDIA_EVENTS) el.addEventListener(type, t.onMedia);
    // The window put in full screen by the worker: once it was the screen, ceasing to be (F11)
    // leaves the theater.
    t.onResize = () => {
      placeCaption(t);
      reportBoxes(t);
      if (!t.windowFull) return;
      if (viewportIsScreen()) t.wasScreen = true;
      else if (t.wasScreen) closeTheater();
    };
    window.addEventListener('resize', t.onResize);
    window.addEventListener('keydown', onTheaterKey, true);
    document.addEventListener('fullscreenchange', onFullscreenChange);

    // Full screen: the page's own, when it is already (the player goes inside that element and
    // covers it), else the player's, else the window's. Never one full screen over another: on
    // leaving, the page (YouTube) missed the end of its own and stayed in its full-screen layout.
    const siteFull = document.fullscreenElement;
    (siteFull ?? document.documentElement).appendChild(host);
    host.focus({ preventScroll: true });
    if (siteFull) {
      t.full = siteFull;
    } else if (gesture) {
      t.full = host;
      host.requestFullscreen({ navigationUI: 'hide' }).catch(() => {
        t.full = null;
        windowFullscreen(true);
      });
    } else {
      windowFullscreen(true);
    }

    // Each frame the video presents is sent to the app and drawn, or kept to be drawn late; a
    // frame callback that stops coming (an element the browser no longer presents under the
    // player) is covered by the animation loop, which takes whatever the element shows now. The
    // loop draws the kept frames as their sound is heard.
    const onFrame = (now, meta) => {
      if (theater !== t) return;
      t.lastFrame = performance.now();
      onVideoFrame(t, meta);
      t.vfc = el.requestVideoFrameCallback?.(onFrame) ?? 0;
    };
    t.vfc = el.requestVideoFrameCallback?.(onFrame) ?? 0;
    const loop = () => {
      if (theater !== t) return;
      if (!el.isConnected) return closeTheater();
      if (!el.paused && performance.now() - t.lastFrame > 250) onVideoFrame(t, null);
      if (t.pic?.queue.length) {
        if (delaying(t)) showDue(t);
        else dropPictures(t);
      }
      if (t.pic) trimSlots(t.pic);
      holdForModel(t);
      showSpinner(t);
      readCaptions(t);
      showProgress();
      t.raf = requestAnimationFrame(loop);
    };
    t.raf = requestAnimationFrame(loop);
    t.timer = setInterval(() => {
      if (performance.now() - t.lastMove > CONTROLS_MS) hideControls();
      if (++t.ticks % 4 === 0) {
        // Once a second anyway: boxes the watch cannot see change (a class given later, a style
        // set above the page's player).
        t.captionsStale = t.captionsDirty = true;
      }
      placeCaption(t);
      reportBoxes(t);
      reportTheater(t, false);
      if (t.ticks % 2 === 0) sendState(true);
    }, 250);
    watchCaptions(t);
    clockSamples = [];
    theta = clockError = NaN;
    for (let i = 0; i < CLOCK_SAMPLES; i++) setTimeout(pingClock, i * 60);
    t.clock = setInterval(pingClock, CLOCK_MS);
    onVideoFrame(t, null);
    showControls();
    reportTheater(t, true);
    sendState(true);
    sendSoon();
  }

  function closeTheater() {
    const t = theater;
    if (!t) return;
    theater = null;
    t.el.cancelVideoFrameCallback?.(t.vfc);
    cancelAnimationFrame(t.raf);
    clearInterval(t.timer);
    clearInterval(t.clock);
    for (const type of MEDIA_EVENTS) t.el.removeEventListener(type, t.onMedia);
    unwatchCaptions(t);
    holdSound(false);
    // The kept frames' GPU memory goes at once, not when the page is collected.
    t.pic?.gl.getExtension('WEBGL_lose_context')?.loseContext();
    window.removeEventListener('resize', t.onResize);
    window.removeEventListener('keydown', onTheaterKey, true);
    document.removeEventListener('fullscreenchange', onFullscreenChange);
    // Back to the page in its window, also when the theater was opened in the page's own full
    // screen: the page sees its full screen end as if its own button did it.
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    if (t.windowFull) post({ type: 'windowFullscreen', on: false });
    t.host.remove();
    post({ type: 'theater', id: t.id, open: false });
    sendSoon();
  }

  /** Asks the worker to put the browser window in full screen (no click to allow the page's own). */
  function windowFullscreen(on) {
    if (theater) theater.windowFull = on;
    post({ type: 'windowFullscreen', on });
  }

  /** The theater's full screen ended (Esc, or the page left its own): so does the theater. */
  function onFullscreenChange() {
    const t = theater;
    if (!t || !t.full) return;
    if (document.fullscreenElement === t.full) reportTheater(t, false);
    else closeTheater();
  }

  function theaterAction(act, target) {
    const t = theater;
    if (!t) return;
    const el = t.el;
    switch (act) {
      case 'play':
        // Played while it waits for a model: the user's choice, kept.
        if (t.modelHold === 'paused') t.modelHold = 'left';
        if (el.paused || el.ended) el.play().catch(() => {});
        else el.pause();
        break;
      case 'back':
      case 'forward':
        if (Number.isFinite(el.duration))
          el.currentTime = Math.min(Math.max(0, el.currentTime - behind(t) + (act === 'back' ? -SEEK_STEP : SEEK_STEP)), el.duration);
        break;
      case 'mute':
        el.muted = !el.muted;
        break;
      case 'settings':
        t.panel.hidden = !t.panel.hidden;
        renderPanel();
        break;
      case 'exit':
        closeTheater();
        return;
      case 'cmd':
        // The 3D panel's buttons: the app changes its settings and tells the new values back.
        if (target?.dataset.command) post({ type: 'command', name: target.dataset.command });
        break;
    }
    wake();
  }

  function onTheaterKey(e) {
    if (!theater || e.ctrlKey || e.altKey || e.metaKey) return;
    const act = {
      ' ': 'play', k: 'play', K: 'play', MediaPlayPause: 'play',
      ArrowLeft: 'back', j: 'back', J: 'back', ArrowRight: 'forward', l: 'forward', L: 'forward',
      m: 'mute', M: 'mute', Escape: 'exit',
    }[e.key];
    if (!act) return;
    // The page under the player must not act on the key too.
    e.preventDefault();
    e.stopImmediatePropagation();
    theaterAction(act);
  }

  function wake() {
    if (!theater) return;
    theater.lastMove = performance.now();
    showControls();
  }

  /** Shows the bar (and the panel, if open) and tells the app at once: it draws them from the live screen. */
  function showControls() {
    const t = theater;
    if (!t) return;
    setIcon(t.play, t.el.paused || t.el.ended ? 'play' : 'pause');
    setIcon(t.mute, t.el.muted ? 'muted' : 'volume');
    if (!t.controlsShown) {
      t.controlsShown = true;
      t.bar.hidden = false;
      t.stage.classList.remove('idle');
    }
    // Just above the bar, wherever it sits.
    t.panel.style.bottom = `${Math.round(t.stage.getBoundingClientRect().bottom - t.bar.getBoundingClientRect().top + 12)}px`;
    showProgress();
    placeCaption(t);
    reportBoxes(t);
  }

  /**
   * A button's icon, replaced only when it changes. showControls runs on every mouse move: a
   * play button rebuilt each time lost the click whenever the mouse moved between press and
   * release (the pressed element was gone).
   */
  function setIcon(button, name) {
    if (button.dataset.icon === name) return;
    button.dataset.icon = name;
    button.innerHTML = icon(name);
  }

  /**
   * While the app loads a depth model (another model or size chosen, or its first load), the
   * video waits, paused, with the spinner: the model takes no frames until it is ready, so the
   * ones played meanwhile would have no depth. It plays on once the model is ready. A video
   * paused already, ended or live is left as it is, and so is one the user plays meanwhile.
   */
  function holdForModel(t) {
    const el = t.el;
    if (appStatus.loading) {
      if (t.modelHold !== null) return;
      t.modelHold = !el.paused && !el.ended && el.duration !== Infinity ? 'paused' : 'left';
      if (t.modelHold === 'paused') el.pause();
    } else if (t.modelHold !== null) {
      if (t.modelHold === 'paused' && el.paused && !el.ended) el.play().catch(() => {});
      t.modelHold = null;
    }
  }

  /**
   * The spinner while a new delay line fills, and after a seek until its first frame shows (not
   * while paused: the bar says that), and while the video waits for a model (holdForModel).
   */
  function showSpinner(t) {
    if (t.waitFor !== null && !delaying(t)) t.waitFor = null;
    const on = t.modelHold === 'paused' || (!!context && (t.fillUntil > context.currentTime || t.waitFor !== null) && !t.el.paused);
    if (t.spinner.hidden !== on) return;
    t.spinner.hidden = !on;
    reportBoxes(t);
  }

  /** The bar stays while the video is paused, the panel is open, or the mouse is on it or dragging. */
  function hideControls() {
    const t = theater;
    if (!t || !t.controlsShown) return;
    if (t.el.paused || !t.panel.hidden || t.dragging !== null || t.bar.matches(':hover')) return;
    t.controlsShown = false;
    t.bar.hidden = true;
    t.stage.classList.add('idle');
    placeCaption(t);
    reportBoxes(t);
  }

  function seekFraction(e) {
    const r = theater.seek.getBoundingClientRect();
    return r.width > 0 ? Math.min(Math.max((e.clientX - r.left) / r.width, 0), 1) : 0;
  }

  function showProgress() {
    const t = theater;
    if (!t || !t.controlsShown) return;
    const el = t.el, live = !Number.isFinite(el.duration) || el.duration <= 0;
    t.seek.hidden = live;
    // The time seen and heard, which the delay keeps behind the video's own.
    const seen = Math.max(0, el.currentTime - behind(t));
    const fraction = t.dragging ?? (live ? 0 : seen / el.duration);
    t.done.style.width = `${fraction * 100}%`;
    t.knob.style.left = `${fraction * 100}%`;
    const now = live ? seen : fraction * el.duration;
    // Written only when it changes: every frame it replaced the text node for a change once a second.
    const text = live ? clock(now) : `${clock(now)} / ${clock(el.duration)}`;
    if (t.time.textContent !== text) t.time.textContent = text;
  }

  function clock(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) return '--:--';
    const s = Math.floor(seconds), h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, r = String(s % 60).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${r}` : `${m}:${r}`;
  }

  /**
   * A frame the video presents (<meta>: the frame callback's; null: whatever it shows now): with
   * the page's caption as it is with it, sent to the app for the depth model, and drawn now, or
   * kept to be drawn as late as the sound.
   */
  function onVideoFrame(t, meta) {
    const mediaTime = meta ? meta.mediaTime : t.el.currentTime;
    if (!meta && mediaTime === t.liveTime) return;
    t.liveTime = mediaTime;
    readCaptions(t);
    sendPicture(t, mediaTime);
    if (delaying(t)) keepFrame(t, meta);
    else drawLive(t, meta);
  }

  /** Draws the video's current picture on the canvas, stretched as the app's setting says. */
  function drawLive(t, meta) {
    if (!sizePicture(t)) return;
    if (t.pic) {
      if (!uploadVideo(t)) return;
      glPass(t.pic, 'copy', null, t.canvas.width, t.canvas.height, t.pic.live);
      releaseShown(t.pic);
    } else {
      try {
        t.ctx.drawImage(t.el, 0, 0, t.canvas.width, t.canvas.height);
      } catch {
        return; // not decodable now (no frame yet): the last one stays
      }
    }
    presented(t, meta ? meta.mediaTime : t.el.currentTime, t.line, meta);
    showCaption(t, t.liveCaption);
  }

  /**
   * Tells the app a new frame is on the canvas: its count, its time in the video and its line,
   * and when it is shown, on the app's clock (drawn now, it is on the screen with the page's next frame).
   */
  function presented(t, mediaTime, line, meta) {
    if (mediaTime === t.drawnTime && line === t.drawnLine) return;
    t.drawnTime = mediaTime;
    t.drawnLine = line;
    t.drawn++;
    if (!Number.isFinite(theta)) return;   // not on the app's clock yet
    const display = meta ? meta.expectedDisplayTime : performance.now() + 1000 / 60;
    post({ type: 'frame', id: t.id, presented: t.drawn, media: mediaTime, line, display: display + theta,
      width: t.canvas.width, height: t.canvas.height });
  }

  /**
   * Sizes the canvas: with kept frames, the picture's size on the screen; without, the video's
   * own (stretched). Frames are kept at the smaller of the two in each direction: a 720p video
   * on a 1080p screen at its own 1280×720 (less than half the memory, nothing lost: the show pass
   * scales it as the canvas would), a 4K one at the screen's. False while the video has no picture yet.
   */
  function sizePicture(t) {
    const el = t.el, vw = el.videoWidth, vh = el.videoHeight;
    if (!vw || !vh) return false;
    const stretch = Number(appStatus.settings?.stretch) > 0 ? Number(appStatus.settings.stretch) : 1;
    let w = Math.round(vw * stretch), h = vh;
    if (t.pic) {
      const box = t.stage.getBoundingClientRect(), dpr = devicePixelRatio || 1;
      const aspect = vw * stretch / vh;
      let cw = box.width, ch = cw / aspect;
      if (ch > box.height) {
        ch = box.height;
        cw = ch * aspect;
      }
      w = Math.max(2, Math.round(cw * dpr));
      h = Math.max(2, Math.round(ch * dpr));
    }
    if (t.canvas.width !== w || t.canvas.height !== h) {
      t.canvas.width = w;
      t.canvas.height = h;
      // A new size clears the canvas: the kept frame on it again (frames kept at another size are drawn scaled).
      if (t.pic?.shown) glPass(t.pic, 'show', null, w, h, t.pic.shown.y.tex, t.pic.shown.uv.tex);
      placeCaption(t);
    }
    if (t.pic) {
      // Also when only the video's size changes (a stream switching quality).
      t.pic.width = Math.min(w, vw);
      t.pic.height = Math.min(h, vh);
    }
    return true;
  }

  // -- The picture, late with the sound
  // While the sound is delayed the player shows the picture as late itself, so the screen is
  // always the finished picture: the video late, the bar, the panel and the spinner over it
  // live, and the app shows that screen as it is. (The app used to delay the captured screen,
  // where the page had already merged video and controls: those came seconds late, and patched in
  // from the live screen they showed the video at another time, a see-through hole that stayed
  // where a panel had been.) Each frame the video presents is kept in GPU memory as luma and
  // half-size chroma (1.5 bytes a pixel, at the size it is shown) with the time on the audio
  // clock, and drawn when its sound is heard: that clock stops while the sound is held (a pause),
  // so the picture holds with it.

  const PICTURE_BUDGET = 800 * 1024 * 1024;  // bytes of kept frames at most (the longest buffer, 4 s of 1080p at 60 fps: 746 MB)
  const PICTURE_LEAD = 0.035;                // s from drawing to the glasses: the page's next frame, the app's capture and present
  const SPARE_SLOTS = 4;                     // free slots kept beyond the fewest needed (trimSlots)

  const GL_VERTEX = `#version 300 es
    out vec2 uv;
    void main() {
      vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
      uv = p;
      gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    }`;
  // The video texture's first row is the picture's top; everything drawn here has it at the bottom.
  const glFragment = body => `#version 300 es
    precision highp float;
    in vec2 uv;
    out vec4 o;
    uniform sampler2D a, b;
    const vec3 K = vec3(0.2126, 0.7152, 0.0722);
    vec3 video() { return texture(a, vec2(uv.x, 1.0 - uv.y)).rgb; }
    void main() { ${body} }`;
  const GL_PROGRAMS = {
    copy: 'o = vec4(video(), 1.0);',
    luma: 'o = vec4(dot(video(), K), 0.0, 0.0, 1.0);',
    chroma: 'vec3 c = video(); float y = dot(c, K); o = vec4((c.b - y) / 1.8556 + 0.5, (c.r - y) / 1.5748 + 0.5, 0.0, 1.0);',
    show: 'float y = texture(a, uv).r; vec2 c = texture(b, uv).rg - 0.5; float r = y + 1.5748 * c.y, bl = y + 1.8556 * c.x; ' +
      'o = vec4(r, (y - 0.2126 * r - 0.0722 * bl) / 0.7152, bl, 1.0);',
  };

  /** The canvas's WebGL 2 drawing and kept frames, or null without WebGL 2. */
  function createPicture(canvas) {
    let gl = null;
    try {
      gl = canvas.getContext('webgl2', { alpha: false, antialias: false, depth: false, stencil: false, powerPreference: 'high-performance' });
    } catch {
      gl = null;
    }
    if (!gl) return null;
    const shader = (type, text) => {
      const s = gl.createShader(type);
      gl.shaderSource(s, text);
      gl.compileShader(s);
      return s;
    };
    const vertex = shader(gl.VERTEX_SHADER, GL_VERTEX);
    const programs = {};
    for (const [name, body] of Object.entries(GL_PROGRAMS)) {
      const p = gl.createProgram();
      gl.attachShader(p, vertex);
      gl.attachShader(p, shader(gl.FRAGMENT_SHADER, glFragment(body)));
      gl.linkProgram(p);
      if (!gl.getProgramParameter(p, gl.LINK_STATUS)) return null;
      gl.useProgram(p);
      gl.uniform1i(gl.getUniformLocation(p, 'a'), 0);
      gl.uniform1i(gl.getUniformLocation(p, 'b'), 1);
      programs[name] = p;
    }
    gl.bindVertexArray(gl.createVertexArray());
    // The video at its own size, with mipmaps: drawn smaller (4K into the screen's size) without shimmer.
    const live = glTexture(gl, gl.LINEAR_MIPMAP_LINEAR);
    return { gl, programs, live, width: 0, height: 0, slots: [], free: [], queue: [], shown: null, leastFree: 0, trimmedAt: performance.now() };
  }

  function glTexture(gl, minFilter) {
    const tex = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, tex);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, minFilter);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return tex;
  }

  function glPass(pic, program, framebuffer, width, height, a, b) {
    const gl = pic.gl;
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.viewport(0, 0, width, height);
    gl.useProgram(pic.programs[program]);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, a);
    if (b) {
      gl.activeTexture(gl.TEXTURE1);
      gl.bindTexture(gl.TEXTURE_2D, b);
    }
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  }

  /** The video's current picture into the live texture; false when there is none, or the page may not read it (then without WebGL from now on). */
  function uploadVideo(t) {
    if (t.el.readyState < 2) return false;
    const gl = t.pic.gl;
    try {
      gl.bindTexture(gl.TEXTURE_2D, t.pic.live);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, t.el);
      gl.generateMipmap(gl.TEXTURE_2D);
      return true;
    } catch {
      fallBackTo2d(t);
      return false;
    }
  }

  /** A canvas drawn with 2D and no kept frames: the sound is not delayed for it (see delaying). */
  function fallBackTo2d(t) {
    const canvas = document.createElement('canvas');
    t.canvas.replaceWith(canvas);
    t.pic?.gl.getExtension('WEBGL_lose_context')?.loseContext();
    t.pic = null;
    t.canvas = canvas;
    t.ctx = canvas.getContext('2d', { alpha: false });
    sendState(true);
  }

  /** A free place for a frame at the size frames are kept at, made while the budget allows; null when full. */
  function takeSlot(pic) {
    while (pic.free.length) {
      const slot = pic.free.pop();
      pic.leastFree = Math.min(pic.leastFree, pic.free.length);
      if (slot.y.w === pic.width && slot.y.h === pic.height) return slot;
      // Of a size before (the stretch or the video's size changed): its memory goes.
      deleteSlot(pic, slot);
    }
    pic.leastFree = 0;
    const bytes = slot => slot.y.w * slot.y.h * 1.5;
    if (pic.slots.reduce((sum, slot) => sum + bytes(slot), 0) + pic.width * pic.height * 1.5 > PICTURE_BUDGET) return null;
    const gl = pic.gl;
    const plane = (format, w, h) => {
      const tex = glTexture(gl, gl.LINEAR);
      gl.texStorage2D(gl.TEXTURE_2D, 1, format, w, h);
      const fb = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
      return { tex, fb, w, h };
    };
    const slot = { y: plane(gl.R8, pic.width, pic.height), uv: plane(gl.RG8, (pic.width + 1) >> 1, (pic.height + 1) >> 1) };
    pic.slots.push(slot);
    return slot;
  }

  function deleteSlot(pic, slot) {
    const gl = pic.gl;
    for (const plane of [slot.y, slot.uv]) {
      gl.deleteFramebuffer(plane.fb);
      gl.deleteTexture(plane.tex);
    }
    pic.slots.splice(pic.slots.indexOf(slot), 1);
  }

  /**
   * Gives back the memory of the free slots that a whole stretch of the delay and a second more
   * never needed (the fewest free in it, less a few spare): after the delay shortens (4 s to 2 s:
   * half the frames) or ends. A seek's refill takes them back within the stretch, so it keeps them.
   */
  function trimSlots(pic) {
    const now = performance.now();
    if (now - pic.trimmedAt < (wantedPicture + 1) * 1000) return;
    for (let n = Math.min(pic.leastFree, pic.free.length) - SPARE_SLOTS; n > 0; n--) deleteSlot(pic, pic.free.shift());
    pic.leastFree = pic.free.length;
    pic.trimmedAt = now;
  }

  /** The picture is delayed: kept frames, a delay asked for, and the sound going through its delay line. */
  function delaying(t) {
    return !!t.pic && wantedPicture > 0 && !!context && !!media.get(t.el)?.node;
  }

  /** The audio clock <ahead> s from now, s; it stands still while the sound is held. */
  function soundClock(ahead) {
    return context.state === 'running' ? context.currentTime + ahead : context.currentTime;
  }

  /** Keeps the video's current picture, stamped with the audio clock at the moment it is presented. */
  function keepFrame(t, meta) {
    const pic = t.pic;
    if (!sizePicture(t) || !uploadVideo(t)) return;
    let slot = takeSlot(pic);
    if (!slot) {
      // Over the budget (a long delay at a high frame rate): every other frame not yet shown goes.
      pic.queue = pic.queue.filter((entry, i) => {
        if (i % 2 === 1 || i === pic.queue.length - 1) return true;
        pic.free.push(entry.slot);
        return false;
      });
      slot = takeSlot(pic);
      if (!slot) return;
    }
    glPass(pic, 'luma', slot.y.fb, slot.y.w, slot.y.h, pic.live);
    glPass(pic, 'chroma', slot.uv.fb, slot.uv.w, slot.uv.h, pic.live);
    const ahead = meta ? Math.max(0, (meta.expectedDisplayTime - performance.now()) / 1000) : 0;
    pic.queue.push({ slot, at: soundClock(ahead), media: meta ? meta.mediaTime : t.el.currentTime, line: t.line, caption: t.liveCaption });
    if (t.preview) showKept(t, pic.queue.length - 1);
  }

  /** Draws the newest kept frame whose sound is heard by the time it reaches the glasses. */
  function showDue(t) {
    const queue = t.pic.queue;
    const due = soundClock(1 / 60) + PICTURE_LEAD - wantedPicture;
    let k = -1;
    while (k + 1 < queue.length && queue[k + 1].at <= due) k++;
    if (k >= 0) showKept(t, k);
  }

  function showKept(t, k) {
    const pic = t.pic, entry = pic.queue[k];
    glPass(pic, 'show', null, t.canvas.width, t.canvas.height, entry.slot.y.tex, entry.slot.uv.tex);
    for (const old of pic.queue.splice(0, k)) pic.free.push(old.slot);
    pic.queue.shift();
    releaseShown(pic);
    // The frame on the canvas stays kept, to draw it again if the canvas changes size.
    pic.shown = entry.slot;
    if (t.waitFor !== null && entry.line >= t.waitFor) t.waitFor = null;
    presented(t, entry.media, entry.line, null);
    showCaption(t, entry.caption);
  }

  function releaseShown(pic) {
    if (pic.shown) pic.free.push(pic.shown);
    pic.shown = null;
  }

  /** Forgets the kept frames not shown yet (a seek: the old place's; the delay off). */
  function dropPictures(t) {
    if (!t.pic) return;
    for (const entry of t.pic.queue) t.pic.free.push(entry.slot);
    t.pic.queue = [];
  }

  // -- The frames for the depth model
  // Each frame the video presents goes to the app as it plays, at the model's input size, so its
  // depth is ready before the player shows it late. Only to the app on this PC, as the capture
  // of the screen does; not for a video the page may not read (another site's, without CORS).

  function sendPicture(t, mediaTime) {
    const size = appStatus.picture;
    if (!size || t.picturesBlocked || t.picturesOut >= 2 || t.el.readyState < 2) return;
    const w = size.width, h = size.height;
    if (t.small?.width !== w || t.small?.height !== h) {
      t.small = new OffscreenCanvas(w, h);
      t.smallCtx = t.small.getContext('2d', { alpha: false });
      t.smallCtx.imageSmoothingQuality = 'high';
    }
    try {
      t.smallCtx.drawImage(t.el, 0, 0, w, h);
    } catch {
      return;
    }
    const line = t.line;
    t.picturesOut++;
    t.small.convertToBlob({ type: 'image/jpeg', quality: 0.9 })
      .then(base64)
      .then(jpeg => {
        if (theater === t) post({ type: 'picture', id: t.id, line, media: mediaTime, width: w, height: h, jpeg });
      })
      .catch(() => {
        // A video the page may not read: the app takes the depth from the screen instead.
        t.picturesBlocked = true;
        sendState(true);
      })
      .finally(() => { t.picturesOut--; });
  }

  /** A blob as base64, encoded by the browser (each frame's JPEG went through JS strings twice, and btoa). */
  function base64(blob) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result.slice(reader.result.indexOf(',') + 1));
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(blob);
    });
  }

  /** The video's playback state: at once on a change, and twice a second so the app knows it is current. */
  function sendState(force) {
    const t = theater;
    if (!t) return;
    const el = t.el;
    const message = {
      type: 'state', id: t.id, paused: el.paused || el.ended, seeking: el.seeking, seeks: media.get(el)?.seeks ?? 0,
      rate: el.playbackRate, clockError: Number.isFinite(clockError) ? Math.round(clockError * 100) / 100 : null,
      // A video with no end (a live stream) is never delayed; the player can send its frames for
      // the depth model and show them late (WebGL, a video it may read), also while the app wants
      // none (a model loading): reported only while it wanted them, the app took the player's
      // pause for the load as a video it could not delay and dropped the delay, which then
      // filled again from nothing (up to 4 s of spinner after each model change).
      live: el.duration === Infinity, pictures: !t.picturesBlocked && !!t.pic,
    };
    const text = JSON.stringify(message);
    if (!force && text === t.state) return;
    t.state = text;
    post(message);
  }

  /**
   * Where the player's picture is, in this frame's CSS px with the px ratio, and whether the
   * viewport is the whole screen (the player's or the window's full screen): only then is that
   * also its place on the screen.
   */
  function reportTheater(t, force) {
    const picture = theaterPicture(t);
    const round = v => Math.round(v * 10) / 10;
    // An element full screen fills the screen by definition (Edge's outer size need not match).
    const full = t.full !== null && document.fullscreenElement === t.full;
    const message = {
      type: 'theater', id: t.id, open: true,
      picture: picture ? { x: round(picture.x), y: round(picture.y), width: round(picture.width), height: round(picture.height) } : null,
      dpr: Math.round(devicePixelRatio * 1000) / 1000,
      onScreen: full || viewportIsScreen(),
      // What that was decided from, for the app's log.
      view: { full, outerWidth, outerHeight, innerWidth, innerHeight, screenWidth: screen.width, screenHeight: screen.height },
    };
    const text = JSON.stringify(message);
    if (!force && text === t.reported) return;
    t.reported = text;
    post(message);
  }

  /**
   * Whether this frame's viewport is the whole screen (the window is full screen): the viewport
   * the screen's size, or (page zoom changes the viewport's CSS px, not the screen's) the window
   * as big as the screen with a viewport of the screen's shape (no tab strip or toolbar left).
   * Edge's outer size in full screen can be a few px off the screen's (1489×832 against 1501×844).
   */
  function viewportIsScreen() {
    if (Math.abs(innerWidth - screen.width) <= 2 && Math.abs(innerHeight - screen.height) <= 2) return true;
    if (Math.abs(outerWidth - screen.width) > 2 || Math.abs(outerHeight - screen.height) > 2) return false;
    return innerHeight > 0 && Math.abs(innerWidth / innerHeight - screen.width / screen.height) < 0.01;
  }

  /**
   * The boxes the player draws over its picture, sent when they change: the bar and the panel
   * (which the app draws live and flat) and the caption's lines (which it keeps flat).
   */
  function reportBoxes(t) {
    const boxes = [];
    const add = (r, kind) => {
      if (r.width >= 2 && r.height >= 2)
        boxes.push({ x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height), kind });
    };
    if (t.controlsShown) add(t.bar.getBoundingClientRect(), 'control');
    if (!t.panel.hidden) add(t.panel.getBoundingClientRect(), 'panel');
    if (!t.spinner.hidden) add(t.spinner.getBoundingClientRect(), 'control');
    for (const r of t.caption.firstChild?.getClientRects() ?? []) add(r, 'caption');
    const text = JSON.stringify(boxes);
    if (text === t.boxes) return;
    t.boxes = text;
    // When they changed, on the app's clock: the app gives each frame the boxes it shows.
    post({ type: 'overlays', id: t.id, boxes, at: Number.isFinite(theta) ? performance.now() + theta : null });
  }

  /** The 3D settings panel, from the values the app last sent. */
  function renderPanel() {
    const t = theater;
    if (!t || t.panel.hidden) return;
    const s = appStatus.settings;
    if (!s) {
      t.panel.innerHTML = '<div class="value">The 3D settings come from the ImmersiveSBS app.</div>';
      t.panelBuilt = false;
    } else {
      // Built once, then only its values change: rebuilt on every new value, a button pressed
      // again quickly was gone between press and release, and the click was lost.
      if (!t.panelBuilt) {
        const step = (key, less, more) =>
          `<div class="line"><button class="round" data-act="cmd" data-command="${less}">−</button>` +
          `<div class="value" data-key="${key}"></div><button class="round" data-act="cmd" data-command="${more}">+</button></div>`;
        t.panel.innerHTML =
          step('model', 'modelPrevious', 'modelNext') +
          step('size', 'sizePrevious', 'sizeNext') +
          step('depth', 'depthLess', 'depthMore') +
          step('plane', 'planeBack', 'planeForward') +
          step('timing', 'timingLess', 'timingMore') +
          step('stretch', 'stretchLess', 'stretchMore') +
          '<div class="line"><button class="wide" data-act="cmd" data-command="layoutNext" data-key="layout"></button></div>';
        t.panelBuilt = true;
      }
      const plane = Number(s.plane);
      const layout = { sideBySide: 'side-by-side 3D, shown as is', topBottom: 'top-bottom 3D, shown as is' }[s.layout] ?? '2D, converted';
      const values = {
        model: `Model:  ${s.model ?? ''}`,
        size: `Resolution:  ${s.size ?? ''}`,
        depth: `Depth:  ${Number(s.depth).toFixed(1)}`,
        plane: `Screen plane:  ${plane.toFixed(2)} (${plane < 0.5 ? 'behind' : plane > 0.5 ? 'in front' : 'balanced'})`,
        timing: `Timing:  ${s.timing ?? ''}`,
        stretch: `Stretch:  ×${Number(s.stretch ?? 1).toFixed(2)}`,
        layout: `Video:  ${layout}`,
      };
      for (const el of t.panel.querySelectorAll('[data-key]')) {
        const text = values[el.dataset.key];
        if (el.textContent !== text) el.textContent = text;
      }
    }
    // New values can change its size.
    reportBoxes(t);
  }

  // -- The page's captions
  // The page's captions, shown in the player: the text of its caption boxes (the page keeps
  // drawing them under the player) and the element's native cues. Read here, never sent.
  //
  // They are read only when they may have changed. A read makes the page lay itself out
  // (visibility, boxes, innerText), and the page under the player changes every frame (its
  // controls, its progress), so reading on every frame drawn cost the page a forced layout 25–60
  // times a second for text that changes every few seconds. A MutationObserver on the page's
  // player marks them changed when a caption box (or anything holding one) does, a text track's
  // cue change does the same, and the next animation frame reads them once.

  /** Starts watching the page's captions for the open theater. */
  function watchCaptions(t) {
    t.onCaptionChange = () => { t.captionsDirty = true; };
    t.captionObserver = new MutationObserver(records => onCaptionMutations(t, records));
    const tracks = t.el.textTracks;
    t.onTracks = () => {
      for (const track of tracks) {
        if (t.tracks.includes(track)) continue;
        t.tracks.push(track);
        track.addEventListener('cuechange', t.onCaptionChange);
      }
      t.captionsDirty = true;
    };
    tracks.addEventListener('addtrack', t.onTracks);
    tracks.addEventListener('change', t.onCaptionChange);   // a track shown or hidden
    t.onTracks();
  }

  function unwatchCaptions(t) {
    t.captionObserver?.disconnect();
    t.el.textTracks.removeEventListener('addtrack', t.onTracks);
    t.el.textTracks.removeEventListener('change', t.onCaptionChange);
    for (const track of t.tracks) track.removeEventListener('cuechange', t.onCaptionChange);
  }

  /** What the page changed in its player: a caption box's text, look or presence, or a new box. */
  function onCaptionMutations(t, records) {
    for (const r of records) {
      if (t.captionsDirty && t.captionsStale) return;
      if (r.type === 'childList' && !t.captionsStale) {
        for (const node of r.addedNodes)
          if (node.nodeType === 1 && (node.matches(CAPTION_SELECTORS) || node.querySelector(CAPTION_SELECTORS))) t.captionsStale = true;
      }
      if (t.captionsDirty) continue;
      const target = r.target.nodeType === 1 ? r.target : r.target.parentElement;
      if (target && t.captions.some(node => node.contains(target) || target.contains(node))) t.captionsDirty = true;
    }
    if (t.captionsStale) t.captionsDirty = true;
  }

  /** Reads the page's captions if they may have changed since the last read. */
  function readCaptions(t) {
    if (t.captionsStale) findCaptionBoxes(t);
    if (t.captionsDirty) updateCaption();
  }

  function updateCaption() {
    const t = theater;
    if (!t) return;
    t.captionsDirty = false;
    const lines = [];
    const video = t.el.getBoundingClientRect();
    const shown = t.captions.filter(node => isCaption(node, video));
    for (const node of shown) {
      if (shown.some(other => other !== node && other.contains(node))) continue;
      const text = (node.innerText ?? '').trim();
      // A list (a menu of subtitle languages the class names did not give away) is no caption.
      if (text && text.split('\n').length <= CAPTION_MAX_LINES) lines.push(text);
    }
    for (const track of t.el.textTracks) {
      if (track.mode !== 'showing' || !track.activeCues) continue;
      for (const cue of track.activeCues) {
        const text = String(cue.text ?? '').replace(/<[^>]*>/g, '').trim();
        if (text) lines.push(text);
      }
    }
    t.liveCaption = [...new Set(lines)].join('\n');
    // Late with the picture while it is delayed: each kept frame carries the caption read with it.
    if (!delaying(t)) showCaption(t, t.liveCaption);
  }

  function showCaption(t, text) {
    if (text === t.shownCaption) return;
    t.shownCaption = text;
    t.caption.replaceChildren();
    if (text) {
      const span = document.createElement('span');
      span.textContent = text;
      t.caption.appendChild(span);
    }
    reportBoxes(t);
  }

  /**
   * Finds the elements that could hold the page's captions, and watches the player they are in:
   * those the selectors match in the page's player around the video (its nearest ancestors, up
   * to CAPTION_ANCESTORS, below the body), less controls and menus. Which of them show a caption
   * is decided on each read.
   */
  function findCaptionBoxes(t) {
    t.captionsStale = false;
    let player = null;
    for (let up = t.el.parentElement, k = 0; up && k < CAPTION_ANCESTORS; up = up.parentElement, k++) {
      if (up === document.body || up === document.documentElement) break;
      player = up;
    }
    if (player !== t.captionPlayer) {
      t.captionObserver.disconnect();
      if (player) {
        t.captionObserver.observe(player, {
          subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'style', 'hidden'],
        });
      }
      t.captionPlayer = player;
    }
    t.captions = [];
    if (player)
      for (const node of player.querySelectorAll(CAPTION_SELECTORS)) {
        // A box holding the video is the player or a wrapper, never its caption.
        if (node.contains(t.el) || node.matches(CAPTION_NOT) || node.querySelector(CAPTION_NOT)) continue;
        if (typeof node.className === 'string' && CAPTION_NOT_CLASS.test(node.className)) continue;
        // Inside a control or a menu (one that does not hold the whole video, as a page's dialog may).
        const around = node.parentElement?.closest(CAPTION_NOT);
        if (around && !around.contains(t.el)) continue;
        t.captions.push(node);
      }
    t.captionsDirty = true;
  }

  /**
   * Whether a caption box shows a caption now: rendered and visible (a hidden box's innerText is
   * all its text: WeTV's hidden subtitle menu), and mostly over the video. Under the player's
   * full screen the page may lay its video out at no size at all (WeTV: 937×0) while its caption
   * stays shown; being in the page's player is the test then.
   */
  function isCaption(node, video) {
    if (!node.isConnected) return false;
    const visible = node.checkVisibility
      ? node.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true, opacityProperty: true, visibilityProperty: true })
      : node.getClientRects().length > 0;
    if (!visible) return false;
    const r = node.getBoundingClientRect();
    if (video.width < 2 || video.height < 2) return r.width > 0 && r.height > 0;
    const x = Math.min(r.right, video.right) - Math.max(r.left, video.left);
    const y = Math.min(r.bottom, video.bottom) - Math.max(r.top, video.top);
    return r.width > 0 && r.height > 0 && x > 0 && y > 0 && x * y >= 0.5 * r.width * r.height;
  }

  /**
   * Keeps the caption inside the picture, where the app holds it flat in front of the scene:
   * low in the picture, above the bar while it shows, never above the picture's middle.
   */
  function placeCaption(t) {
    const pic = theaterPicture(t);
    if (!pic) return;
    const stage = t.stage.getBoundingClientRect();
    const below = stage.bottom - (pic.y + pic.height);
    let bottom = below + 0.1 * pic.height;
    if (t.controlsShown) bottom = Math.max(bottom, stage.bottom - t.bar.getBoundingClientRect().top + 8);
    bottom = Math.min(bottom, below + 0.5 * pic.height);
    const side = 0.06 * pic.width;
    const style = `left: ${Math.round(pic.x - stage.left + side)}px; right: ${Math.round(stage.right - pic.x - pic.width + side)}px; ` +
      `bottom: ${Math.round(bottom)}px; font-size: clamp(16px, ${(pic.height * 0.042).toFixed(1)}px, 44px);`;
    if (style === t.captionStyle) return;
    t.captionStyle = style;
    t.caption.style.cssText = style;
    reportBoxes(t);
  }

  /** Where the canvas shows the picture, in this frame's viewport CSS px. */
  function theaterPicture(t) {
    const box = t.canvas.getBoundingClientRect();
    const vw = t.canvas.width, vh = t.canvas.height;
    if (box.width < 2 || box.height < 2 || !vw || !vh) return null;
    return centred(box.left, box.top, box.width, box.height, vw, vh, Math.min(box.width / vw, box.height / vh));
  }

  // ---- Heartbeat -------------------------------------------------------------------------

  scan();
  setInterval(() => {
    scan();
    send();
  }, REPORT_MS);
})();
