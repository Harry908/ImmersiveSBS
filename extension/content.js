// ImmersiveSBS link: the page script, in every frame. It is part of the ImmersiveSBS app: the app
// finds browser video only through it, and shows browser video in 3D only in its theater.
//
// - It reports the page's video and audio elements (playing, audible, whether their sound can be
//   delayed) and, when the app asks, plays their sound late through Web Audio: element ->
//   DelayNode -> output. The sound never leaves the browser.
// - While the app says the glasses are in 3D mode, it shows a "3D theater" button over a video
//   under the mouse. The theater is a player covering the page, full screen, that draws the
//   video at its own size on a canvas; the app captures that screen and turns it into 3D.
// - While the theater is open it reports, for that video only: where the picture is on the
//   screen, each frame it draws (on the app's clock), its playback state, and the boxes of its
//   own controls and captions (which the app keeps flat).
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
  let context = null;       // AudioContext, made on the first delay
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
        if (theater?.el === el) sendState(false);
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
    let width = el.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    let height = el.clientHeight - parseFloat(style.paddingTop) - parseFloat(style.paddingBottom);
    let x = left, y = top;
    const vw = el.videoWidth, vh = el.videoHeight;
    const fit = style.objectFit || 'contain';
    if (vw > 0 && vh > 0 && (fit === 'contain' || fit === 'scale-down' || fit === 'none')) {
      // The default for video: the picture letterboxed in the box ("none": at its own size).
      const contain = Math.min(width / vw, height / vh);
      const scale = fit === 'contain' ? contain : fit === 'none' ? 1 : Math.min(contain, 1);
      x += (width - vw * scale) / 2;
      y += (height - vh * scale) / 2;
      width = vw * scale;
      height = vh * scale;
    }
    return { x, y, width, height };
  }

  /** What the app needs to know about an element: for the sound, and which video the theater shows. */
  function describe(el, state) {
    const reason = blocker(el) ?? (wanted > 0 && context && context.state !== 'running' ? 'blocked' : null);
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
      queueApply();
    } else if (m?.type === 'app') {
      appConnected = m.connected === true;
      if (!appConnected) showButton(null);
      lastSent = '';
      send();
      if (theater) reportTheater(theater, true);
    } else if (m?.type === 'status') {
      appStatus = { glasses: m.glasses === true, offer: m.offer === true, settings: m.settings ?? null };
      if (!appStatus.glasses || !appStatus.offer) showButton(null);
      renderPanel();
      if (theater) drawFrame();   // the stretch may have changed
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
    try {
      port.postMessage({ type: 'media', media: list });
    } catch {
      port = null;
    }
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

  /** The browser lets audio start only after the page had a click; resolves whether it runs. */
  async function contextRunning() {
    if (!context) {
      context = new AudioContext({ latencyHint: 'interactive' });
      context.onstatechange = sendSoon;
    }
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
        const maxDelay = Math.min(179, want + 1);
        const node = new DelayNode(context, { maxDelayTime: maxDelay, delayTime: want });
        node.maxDelay = maxDelay;
        route(el, state, node);
      }
      state.applied = want;
    } else if (state.applied !== 0 || state.node) {
      route(el, state, null);
    }
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
  // While the app is connected and the glasses are in 3D mode, a "3D theater" button shows over
  // a video the mouse is on. Pressed (a real click in the page, so the page may go full screen),
  // a player covers the page and goes full screen: it draws the video at its own size on a
  // canvas, so the app captures it sharp, not as small as the page showed it, and turns it into
  // 3D in the glasses. Its controls and the page's captions are drawn here and reported as boxes,
  // which the app keeps flat. The original element keeps playing, with its sound (delayed as usual).

  const THEATER_MIN_WIDTH = 160, THEATER_MIN_HEIGHT = 90;
  const CONTROLS_MS = 2500;   // controls fade after this long without the mouse moving
  const SEEK_STEP = 10;       // s, the ← → keys and the back / on buttons
  /** What the app tells: { glasses: in 3D mode, offer: show the button, settings: the 3D panel's values }. */
  let appStatus = { glasses: false, offer: false, settings: null };
  let button = null;          // { host, el (the video under it), timer }
  let theater = null;         // the open player, see openTheater
  let moveQueued = false;

  const ICONS = {
    theater: 'M4 6h16v12H4zM2 4v16h20V4zm5 5h3v2H8v2h2v2H7zm6 0h2.5a2.5 2.5 0 0 1 0 6H13zm2 2v2h.5a1 1 0 0 0 0-2z',
    play: 'M8 5v14l11-7z',
    pause: 'M6 5h4v14H6zm8 0h4v14h-4z',
    back: 'M12 5V1L7 6l5 5V7a6 6 0 1 1-6 6H4a8 8 0 1 0 8-8z',
    forward: 'M12 5V1l5 5-5 5V7a6 6 0 1 0 6 6h2a8 8 0 1 1-8-8z',
    volume: 'M3 9v6h4l5 5V4L7 9zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z',
    muted: 'M16.5 12A4.5 4.5 0 0 0 14 8v2.2l2.45 2.45q.05-.3.05-.65M19 12q0 1.4-.55 2.65l1.5 1.5A9 9 0 0 0 14 3.2v2.1a7 7 0 0 1 5 6.7M4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25A7 7 0 0 1 14 18.7v2.1a9 9 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9zM12 4 9.91 6.09 12 8.18z',
    full: 'M7 14H5v5h5v-2H7zm-2-4h2V7h3V5H5zm12 7h-3v2h5v-5h-2zM14 5v2h3v3h2V5z',
    window: 'M5 16h3v3h2v-5H5zm3-8H5v2h5V5H8zm6 11h2v-3h3v-2h-5zm2-11V5h-2v5h5V8z',
    settings: 'M19.4 13a7.5 7.5 0 0 0 0-2l2.1-1.6-2-3.5-2.5 1a7.4 7.4 0 0 0-1.7-1L15 3.3h-4l-.4 2.6a7.4 7.4 0 0 0-1.7 1l-2.5-1-2 3.5L6.6 11a7.5 7.5 0 0 0 0 2l-2.1 1.6 2 3.5 2.5-1a7.4 7.4 0 0 0 1.7 1l.4 2.6h4l.4-2.6a7.4 7.4 0 0 0 1.7-1l2.5 1 2-3.5zM13 15.5a3.5 3.5 0 1 1 0-7 3.5 3.5 0 0 1 0 7z',
    close: 'M19 6.4 17.6 5 12 10.6 6.4 5 5 6.4l5.6 5.6L5 17.6 6.4 19l5.6-5.6 5.6 5.6 1.4-1.4-5.6-5.6z',
  };
  const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${ICONS[name]}"/></svg>`;

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; font-family: "Segoe UI", system-ui, sans-serif; }
    svg { width: 24px; height: 24px; fill: currentColor; display: block; }
    button { all: unset; cursor: pointer; color: #fff; display: inline-flex; align-items: center; justify-content: center;
      gap: 8px; min-width: 44px; height: 44px; padding: 0 12px; border-radius: 22px; background: rgba(24,24,26,.78);
      border: 1px solid rgba(255,255,255,.35); font-size: 15px; white-space: nowrap; }
    button:hover, button:focus-visible { background: rgba(70,70,74,.92); border-color: #60cdff; }
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
    if (!appConnected || !appStatus.glasses || !appStatus.offer) return null;
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

  /**
   * Opens the player on <el>. <gesture>: called from a click in the page, which lets it go full
   * screen; asked by the app instead, the browser window goes full screen (the worker does it).
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
      .caption { position: absolute; left: 10%; right: 10%; bottom: 20%; text-align: center; pointer-events: none;
        font-size: clamp(18px, 3.2vh, 44px); }
      .caption span { display: inline; white-space: pre-line; color: #fff; background: rgba(8,8,8,.75); padding: 2px 8px;
        font-size: inherit; line-height: 1.35; box-decoration-break: clone; -webkit-box-decoration-break: clone; }
      .top { position: absolute; top: 16px; left: 50%; transform: translateX(-50%); display: flex; gap: 12px; }
      .bar { position: absolute; left: 0; right: 0; bottom: 0; padding: 40px 32px 16px;
        background: linear-gradient(transparent, rgba(0,0,0,.75)); }
      .seek { position: relative; height: 24px; cursor: pointer; touch-action: none; }
      .track { position: absolute; left: 0; right: 0; top: 9px; height: 6px; border-radius: 3px; background: rgba(255,255,255,.35); }
      .seek:hover .track { top: 7px; height: 10px; border-radius: 5px; }
      .done { position: absolute; left: 0; top: 0; bottom: 0; border-radius: inherit; background: #60cdff; }
      .knob { position: absolute; top: 50%; width: 18px; height: 18px; margin: -9px 0 0 -9px; border-radius: 50%; background: #fff; }
      .row { display: grid; grid-template-columns: 1fr auto 1fr; align-items: center; margin-top: 10px; }
      .time { color: #fff; font-size: 16px; font-variant-numeric: tabular-nums; }
      .middle, .right { display: flex; gap: 14px; align-items: center; }
      .right { justify-self: end; }
      .panel { position: absolute; top: 76px; left: 50%; transform: translateX(-50%); padding: 12px; border-radius: 16px;
        background: rgba(24,24,26,.92); border: 1px solid rgba(255,255,255,.25); display: grid; gap: 8px; }
      .panel .line { display: grid; grid-template-columns: 48px 340px 48px; gap: 8px; align-items: center; }
      .panel .value { color: #fff; font-size: 15px; padding: 0 12px; }
      .panel button.wide { grid-column: 1 / 4; justify-content: flex-start; }
      .hidden { display: none !important; }
      .top, .bar { transition: opacity .25s; }
      </style>
      <div class="stage"><canvas></canvas><div class="caption"></div></div>
      <div class="top">
        <button data-act="settings" title="3D settings">${icon('settings')}3D settings</button>
        <button data-act="exit" title="Leave the theater (Esc)">${icon('close')}Exit theater</button>
      </div>
      <div class="panel hidden"></div>
      <div class="bar">
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
            <button class="round" data-act="fullscreen" title="Full screen (F, double click)"></button>
          </div>
        </div>
      </div>`;
    const $ = s => root.querySelector(s);
    const t = {
      el, id: media.get(el).id, host, root, canvas: $('canvas'), stage: $('.stage'), caption: $('.caption'), panel: $('.panel'),
      bar: $('.bar'), top: $('.top'), seek: $('.seek'), done: $('.done'), knob: $('.knob'), time: $('.time'),
      play: $('[data-act="play"]'), mute: $('[data-act="mute"]'), full: $('[data-act="fullscreen"]'),
      ctx: null, vfc: 0, raf: 0, timer: 0, clock: 0, ticks: 0, lastFrame: 0, lastMove: performance.now(), dragging: null,
      clickTimer: 0, windowFull: false, leaving: false, controlsShown: false, player: playerOf(el), shownCaption: '', captionStyle: '',
      drawn: 0, drawnTime: NaN, reported: '', boxes: '', state: '', onMedia: null,
    };
    t.ctx = t.canvas.getContext('2d', { alpha: false });
    theater = t;

    root.addEventListener('click', e => {
      const target = e.target.closest?.('[data-act]');
      if (target) {
        e.stopPropagation();
        theaterAction(target.dataset.act, target);
      }
    });
    t.stage.addEventListener('click', () => {
      // A single click plays or pauses; a double click switches full screen instead.
      clearTimeout(t.clickTimer);
      t.clickTimer = setTimeout(() => theaterAction('play'), 250);
    });
    t.stage.addEventListener('dblclick', () => {
      clearTimeout(t.clickTimer);
      theaterAction('fullscreen');
    });
    host.addEventListener('pointermove', () => wake(), { passive: true });
    for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click', 'dblclick', 'wheel', 'contextmenu'])
      host.addEventListener(type, e => e.stopPropagation());
    // The page under the player must not scroll.
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
      if (el.paused) drawFrame();
      showControls();
    };
    for (const type of ['play', 'pause', 'seeked', 'volumechange', 'loadeddata', 'durationchange', 'ended'])
      el.addEventListener(type, t.onMedia);

    window.addEventListener('keydown', onTheaterKey, true);
    document.addEventListener('fullscreenchange', onFullscreenChange);
    (document.fullscreenElement ?? document.documentElement).appendChild(host);
    host.focus({ preventScroll: true });
    if (gesture && host.requestFullscreen) {
      host.requestFullscreen({ navigationUI: 'hide' }).catch(() => windowFullscreen(true));
    } else {
      windowFullscreen(true);
    }

    // Each frame the video presents is drawn as it comes; a frame callback that stops coming
    // (an element the browser no longer presents under the player) is covered by the
    // animation loop, which draws whatever the element shows now.
    const onFrame = (now, meta) => {
      if (theater !== t) return;
      t.lastFrame = performance.now();
      drawFrame(meta);
      t.vfc = el.requestVideoFrameCallback?.(onFrame) ?? 0;
    };
    t.vfc = el.requestVideoFrameCallback?.(onFrame) ?? 0;
    const loop = () => {
      if (theater !== t) return;
      if (!el.isConnected) return closeTheater();
      if (!el.paused && performance.now() - t.lastFrame > 250) drawFrame();
      showProgress();
      t.raf = requestAnimationFrame(loop);
    };
    t.raf = requestAnimationFrame(loop);
    t.timer = setInterval(() => {
      if (performance.now() - t.lastMove > CONTROLS_MS) fadeControls();
      updateCaption();
      placeCaption(t);
      reportTheater(t, false);
      reportBoxes(t);
      if (++t.ticks % 2 === 0) sendState(true);
    }, 250);
    clockSamples = [];
    theta = clockError = NaN;
    for (let i = 0; i < CLOCK_SAMPLES; i++) setTimeout(pingClock, i * 60);
    t.clock = setInterval(pingClock, CLOCK_MS);
    drawFrame();
    showControls();
    renderPanel();
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
    clearTimeout(t.clickTimer);
    for (const type of ['play', 'pause', 'seeked', 'volumechange', 'loadeddata', 'durationchange', 'ended'])
      t.el.removeEventListener(type, t.onMedia);
    window.removeEventListener('keydown', onTheaterKey, true);
    document.removeEventListener('fullscreenchange', onFullscreenChange);
    if (document.fullscreenElement === t.host) {
      t.leaving = true;
      document.exitFullscreen().catch(() => {});
    }
    if (t.windowFull) post({ type: 'windowFullscreen', on: false });
    t.host.remove();
    post({ type: 'theater', id: t.id, open: false });
    sendSoon();
  }

  /** Asks the worker to put the browser window in full screen (no click to allow the page's own), or back. */
  function windowFullscreen(on) {
    if (theater) theater.windowFull = on;
    post({ type: 'windowFullscreen', on });
  }

  function onFullscreenChange() {
    const t = theater;
    if (!t) return;
    // Esc leaves the browser's full screen by itself; that leaves the theater too. The full
    // screen button only leaves full screen.
    if (document.fullscreenElement !== t.host && !t.leaving && !t.windowFull) return closeTheater();
    t.leaving = false;
    showControls();
    reportTheater(t, false);
  }

  function theaterAction(act, target) {
    const t = theater;
    if (!t) return;
    const el = t.el;
    switch (act) {
      case 'play':
        if (el.paused || el.ended) el.play().catch(() => {});
        else el.pause();
        break;
      case 'back':
      case 'forward':
        if (Number.isFinite(el.duration))
          el.currentTime = Math.min(Math.max(0, el.currentTime + (act === 'back' ? -SEEK_STEP : SEEK_STEP)), el.duration);
        break;
      case 'mute':
        el.muted = !el.muted;
        break;
      case 'fullscreen':
        if (document.fullscreenElement === t.host) {
          t.leaving = true;
          document.exitFullscreen().catch(() => {});
        } else if (t.windowFull) {
          windowFullscreen(false);
        } else {
          t.host.requestFullscreen?.({ navigationUI: 'hide' }).catch(() => windowFullscreen(true));
        }
        break;
      case 'settings':
        t.panel.classList.toggle('hidden');
        renderPanel();
        reportBoxes(t);
        break;
      case 'exit':
        closeTheater();
        return;
      default:
        // The 3D panel's buttons: the app changes its settings and tells the new values back.
        if (target?.dataset.command) post({ type: 'command', name: target.dataset.command });
        break;
    }
    wake();
  }

  function onTheaterKey(e) {
    const t = theater;
    if (!t || e.ctrlKey || e.altKey || e.metaKey) return;
    const act = {
      ' ': 'play', k: 'play', K: 'play', MediaPlayPause: 'play',
      ArrowLeft: 'back', j: 'back', J: 'back', ArrowRight: 'forward', l: 'forward', L: 'forward',
      m: 'mute', M: 'mute', f: 'fullscreen', F: 'fullscreen', Escape: 'exit',
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

  function showControls() {
    const t = theater;
    if (!t) return;
    t.stage.classList.remove('idle');
    t.top.style.opacity = t.bar.style.opacity = '';
    t.top.style.pointerEvents = t.bar.style.pointerEvents = '';
    t.play.innerHTML = icon(t.el.paused || t.el.ended ? 'play' : 'pause');
    t.mute.innerHTML = icon(t.el.muted ? 'muted' : 'volume');
    t.full.innerHTML = icon(document.fullscreenElement === t.host || t.windowFull ? 'window' : 'full');
    t.controlsShown = true;
    placeCaption(t);
    showProgress();
    // The app shows the controls from the live screen where they are: tell it at once.
    reportBoxes(t);
  }

  /** Controls stay while the video is paused, the panel is open or the mouse is on them. */
  function fadeControls() {
    const t = theater;
    if (!t || !t.controlsShown) return;
    if (t.el.paused || !t.panel.classList.contains('hidden') || t.dragging !== null) return;
    if (t.top.matches(':hover') || t.bar.matches(':hover')) return;
    t.top.style.opacity = t.bar.style.opacity = '0';
    t.top.style.pointerEvents = t.bar.style.pointerEvents = 'none';
    t.stage.classList.add('idle');
    t.controlsShown = false;
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
    t.seek.classList.toggle('hidden', live);
    const fraction = t.dragging ?? (live ? 0 : el.currentTime / el.duration);
    t.done.style.width = `${fraction * 100}%`;
    t.knob.style.left = `${fraction * 100}%`;
    const now = live ? el.currentTime : fraction * el.duration;
    t.time.textContent = live ? clock(now) : `${clock(now)} / ${clock(el.duration)}`;
  }

  function clock(seconds) {
    if (!Number.isFinite(seconds) || seconds < 0) return '--:--';
    const s = Math.floor(seconds), h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, r = String(s % 60).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${r}` : `${m}:${r}`;
  }

  /**
   * Draws the video's current picture on the canvas, stretched as the app's setting says, and
   * reports it to the app when it is a new frame: its count, its time in the video and when it
   * is shown, on the app's clock. <meta>: the frame callback's, when it came from one.
   */
  function drawFrame(meta) {
    const t = theater;
    if (!t) return;
    const el = t.el, vw = el.videoWidth, vh = el.videoHeight;
    if (!vw || !vh) return;
    const stretch = Number(appStatus.settings?.stretch) > 0 ? Number(appStatus.settings.stretch) : 1;
    const w = Math.round(vw * stretch), h = vh;
    if (t.canvas.width !== w || t.canvas.height !== h) {
      t.canvas.width = w;
      t.canvas.height = h;
    }
    try {
      t.ctx.drawImage(el, 0, 0, w, h);
    } catch {
      return; // not decodable now (no frame yet): the last one stays
    }
    const mediaTime = meta ? meta.mediaTime : el.currentTime;
    if (mediaTime === t.drawnTime) return;
    t.drawnTime = mediaTime;
    t.drawn++;
    // The page's caption as it is with this frame (the timer alone would show it up to 250 ms late).
    updateCaption();
    if (!Number.isFinite(theta)) return;   // not on the app's clock yet
    // Drawn now, the canvas is on the screen with the next frame of the page.
    const display = meta ? meta.expectedDisplayTime : performance.now() + 1000 / 60;
    post({ type: 'frame', id: t.id, presented: t.drawn, media: mediaTime, display: display + theta, width: w, height: h });
  }

  /** The video's playback state: at once on a change, and twice a second so the app knows it is current. */
  function sendState(force) {
    const t = theater;
    if (!t) return;
    const el = t.el;
    const message = {
      type: 'state', id: t.id, paused: el.paused || el.ended, seeking: el.seeking, seeks: media.get(el)?.seeks ?? 0,
      rate: el.playbackRate, clockError: Number.isFinite(clockError) ? Math.round(clockError * 100) / 100 : null,
    };
    const text = JSON.stringify(message);
    if (!force && text === t.state) return;
    t.state = text;
    post(message);
  }

  /**
   * Where the player's picture is, in this frame's CSS px with the px ratio, and whether the
   * viewport is the whole screen (full screen, the page's or the window's): only then is that
   * also its place on the screen.
   */
  function reportTheater(t, force) {
    const picture = theaterPicture(t);
    const round = v => Math.round(v * 10) / 10;
    // The player full screen (the page's own full screen) fills the screen by definition.
    const full = document.fullscreenElement === t.host;
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
   * Whether this frame's viewport is the whole screen (the page or the window is full screen):
   * the window as big as the screen (screen units, which page zoom does not change) and the
   * viewport of the screen's shape (no tab strip or toolbar left).
   */
  function viewportIsScreen() {
    if (Math.abs(outerWidth - screen.width) > 2 || Math.abs(outerHeight - screen.height) > 2) return false;
    return innerHeight > 0 && Math.abs(innerWidth / innerHeight - screen.width / screen.height) < 0.01;
  }

  /** The boxes of the player's own controls and caption over the picture (the app keeps them flat). */
  function reportBoxes(t) {
    const boxes = [];
    const add = (r, kind) => {
      if (r.width >= 2 && r.height >= 2)
        boxes.push({ x: Math.round(r.left), y: Math.round(r.top), width: Math.round(r.width), height: Math.round(r.height), kind });
    };
    if (t.controlsShown) {
      for (const b of t.root.querySelectorAll('.top button, .bar .row button')) add(b.getBoundingClientRect(), 'control');
      add(t.seek.getBoundingClientRect(), 'control');
      add(t.time.getBoundingClientRect(), 'control');
    }
    if (!t.panel.classList.contains('hidden')) add(t.panel.getBoundingClientRect(), 'control');
    const span = t.caption.firstChild;
    if (span) for (const r of span.getClientRects()) add(r, 'caption');
    const text = JSON.stringify(boxes);
    if (text === t.boxes) return;
    t.boxes = text;
    // When they changed, on the app's clock: the app gives each frame the boxes it shows (a frame
    // early rather than late: a caption shown before its box would bend with the scene).
    post({ type: 'overlays', id: t.id, boxes, at: Number.isFinite(theta) ? performance.now() + theta : null });
  }

  /** The 3D settings panel, from the values the app last sent. */
  function renderPanel() {
    const t = theater;
    if (!t || t.panel.classList.contains('hidden')) return;
    const s = appStatus.settings;
    if (!s) {
      t.panel.innerHTML = '<div class="value">The 3D settings come from the ImmersiveSBS app.</div>';
      return;
    }
    const step = (label, less, more) =>
      `<div class="line"><button class="round" data-act="cmd" data-command="${less}">−</button>` +
      `<div class="value">${label}</div><button class="round" data-act="cmd" data-command="${more}">+</button></div>`;
    const toggle = (label, command) =>
      `<div class="line"><button class="wide" data-act="cmd" data-command="${command}">${label}</button></div>`;
    const esc = v => String(v ?? '').replace(/[&<>"]/g, c => `&#${c.charCodeAt(0)};`);
    const plane = Number(s.plane);
    const layout = { sideBySide: 'side-by-side 3D, shown as is', topBottom: 'top-bottom 3D, shown as is' }[s.layout] ?? '2D, converted';
    t.panel.innerHTML =
      step(`Model:  ${esc(s.model)}`, 'modelPrevious', 'modelNext') +
      step(`Resolution:  ${esc(s.size)}`, 'sizePrevious', 'sizeNext') +
      step(`Depth:  ${Number(s.depth).toFixed(1)}`, 'depthLess', 'depthMore') +
      step(`Screen plane:  ${plane.toFixed(2)} (${plane < 0.5 ? 'behind' : plane > 0.5 ? 'in front' : 'balanced'})`, 'planeBack', 'planeForward') +
      step(`Timing:  ${esc(s.timing)}`, 'timingLess', 'timingMore') +
      step(`Stretch:  ×${Number(s.stretch ?? 1).toFixed(2)}`, 'stretchLess', 'stretchMore') +
      toggle(`Video:  ${layout}`, 'layoutNext') +
      toggle(`Snap depth edges to the picture:  ${s.edgeSnap ? 'on' : 'off'}`, 'edgeSnap') +
      toggle(`Keep subtitles flat:  ${s.captions ? 'on' : 'off'}`, 'captions');
    // New values can change its size; the app shows it from the live screen where it is.
    reportBoxes(t);
  }

  /** The player around a video: its largest ancestor not much bigger than its picture. */
  function playerOf(el) {
    const pic = pictureRect(el);
    const area = pic ? pic.width * pic.height : 0;
    let player = el;
    for (let up = el.parentElement, k = 0; up && k < 8 && area > 0; up = up.parentElement, k++) {
      const r = up.getBoundingClientRect();
      if (r.width * r.height > 1.6 * area) break;
      player = up;
    }
    return player;
  }

  /**
   * The page's captions, shown in the player: its own caption boxes' text (the page keeps
   * drawing them under the player) and the element's native cues. Read here, never sent.
   */
  function updateCaption() {
    const t = theater;
    if (!t) return;
    const lines = [];
    if (t.player !== t.el) {
      const video = t.el.getBoundingClientRect();
      const nodes = [...t.player.querySelectorAll(CAPTION_SELECTORS)].filter(node => isCaption(node, video, t.player));
      for (const node of nodes) {
        if (nodes.some(other => other !== node && other.contains(node))) continue;
        const text = (node.innerText ?? '').trim();
        // A list (a menu of subtitle languages the class names did not give away) is no caption.
        if (text && text.split('\n').length <= CAPTION_MAX_LINES) lines.push(text);
      }
    }
    for (const track of t.el.textTracks) {
      if (track.mode !== 'showing' || !track.activeCues) continue;
      for (const cue of track.activeCues) {
        const text = String(cue.text ?? '').replace(/<[^>]*>/g, '').trim();
        if (text) lines.push(text);
      }
    }
    const text = [...new Set(lines)].join('\n');
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
   * Whether a box the caption selectors matched is a caption shown now: rendered and visible
   * (a hidden box's innerText is all its text: WeTV's hidden subtitle menu), over the video, and
   * not a control, a menu or a settings panel (players name those "subtitle" too).
   */
  function isCaption(node, video, player) {
    const visible = node.checkVisibility
      ? node.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true, opacityProperty: true, visibilityProperty: true })
      : node.getClientRects().length > 0;
    if (!visible) return false;
    const around = node.closest(CAPTION_NOT);
    if ((around && player.contains(around)) || node.querySelector(CAPTION_NOT)) return false;
    if (typeof node.className === 'string' && CAPTION_NOT_CLASS.test(node.className)) return false;
    const r = node.getBoundingClientRect();
    const x = Math.min(r.right, video.right) - Math.max(r.left, video.left);
    const y = Math.min(r.bottom, video.bottom) - Math.max(r.top, video.top);
    return r.width > 0 && r.height > 0 && x > 0 && y > 0 && x * y >= 0.5 * r.width * r.height;
  }

  /**
   * Keeps the caption inside the picture: the app shows the picture's rectangle late (the
   * buffer, matched timing) and the rest of the screen live, so a caption in the black bars
   * would show before its line is spoken, and one across the edge twice. Low in the picture,
   * above the controls while they show, never above the picture's middle.
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
  }

  /** Where the canvas shows the picture, in this frame's viewport CSS px. */
  function theaterPicture(t) {
    const box = t.canvas.getBoundingClientRect();
    const vw = t.canvas.width, vh = t.canvas.height;
    if (box.width < 2 || box.height < 2 || !vw || !vh) return null;
    const scale = Math.min(box.width / vw, box.height / vh);
    const w = vw * scale, h = vh * scale;
    return { x: box.left + (box.width - w) / 2, y: box.top + (box.height - h) / 2, width: w, height: h };
  }

  // ---- Heartbeat -------------------------------------------------------------------------

  scan();
  setInterval(() => {
    scan();
    send();
  }, REPORT_MS);
})();
