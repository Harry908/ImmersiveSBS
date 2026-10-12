// ImmersiveSBS link: the page script, in every frame. It finds the page's video and audio
// elements and reports them (playing, audible, where the picture is) to the worker, and when the
// app asks, plays their sound late through Web Audio: element -> DelayNode -> output. The sound
// never leaves the browser, so it keeps its full quality and its usual output, and a paused or
// seeking video stays in step with the delayed picture by itself (the delay line plays out what
// it holds, just as the picture buffer does).
//
// What cannot be delayed is left alone and reported, so the app can fall back to its own
// capture: protected (DRM) media, media from another origin without CORS (Web Audio would get
// silence from it), and pages where the browser does not let audio start before a click.
//
// For the one video the app follows (protocol 2), the frame holding it also reports each
// presented frame, its playback state the moment it changes, and the boxes the page draws over
// its picture (controls, captions, logos), with frame times already on the app's clock.
'use strict';

(() => {
  const TAG = '__immersiveSbs';
  const PORT_NAME = 'immersivesbs-frame';
  const REPORT_MS = 500;
  const SHADOW_SCAN_MS = 5000;       // while the app is connected
  const IDLE_SHADOW_SCAN_MS = 30000; // while it is not (a page whose only video is in a shadow root)
  const OVERLAY_MS = 250;            // the followed video's overlay probe
  const OVERLAY_GRID = 8;            // probe points across and down the picture
  const CLOCK_MS = 5000;             // clock re-sync
  const CLOCK_SAMPLES = 8;           // the best of this many round trips counts
  // Caption containers of common players; native <track> captions are found from textTracks.
  const CAPTION_SELECTORS = [
    '.ytp-caption-window-container', '.caption-window', '.ytp-caption-segment',   // YouTube
    '.vjs-text-track-display', '.vjs-text-track-cue',                            // Video.js
    '.plyr__captions', '.plyr__caption',                                         // Plyr
    '.jw-captions', '.jw-text-track-container',                                  // JW Player
    '.vp-captions',                                                               // Vimeo
    '[data-a-target="player-captions-container"]', '.player-captions-container',  // Twitch
    '[class*="caption" i]', '[class*="subtitle" i]',
  ].join(',');
  const CONTROL_SELECTORS = 'button, [role="button"], [role="slider"], [class*="control" i], [class*="progress" i], [class*="chrome" i]';

  /** element -> { id, delay node, delay applied (s), seeks } for elements in the page. */
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
  /** Where this frame's viewport sits in the top frame's, CSS px; known at once in the top frame. */
  let frameOffset = window === window.top ? { x: 0, y: 0 } : null;
  let topViewport = null;   // { width, height } of the top frame's viewport, CSS px
  let childAsked = -1e9;    // when a frame inside this one last asked where it is
  const listened = new WeakSet();
  /** The video the app follows, if it is in this frame: { el, id, handle, timer, clock, state, overlays }. */
  let followed = null;
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
    media.set(el, { id: nextId++, node: null, applied: 0, seeks: 0 });
    connectPort();
    if (listened.has(el)) return;
    listened.add(el);
    for (const type of ['play', 'playing', 'pause', 'ended', 'seeking', 'seeked', 'ratechange', 'volumechange', 'loadedmetadata', 'emptied']) {
      el.addEventListener(type, () => {
        const state = media.get(el);
        if (!state) return;
        if (type === 'seeked') state.seeks++;
        if (type === 'playing') queueApply();
        if (followed?.el === el) sendState(false);
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

  /** The video picture inside the element's box (object-fit), in top-frame CSS px, or null. */
  function pictureRect(el) {
    if (!frameOffset || el.tagName !== 'VIDEO') return null;
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
    const round = v => Math.round(v * 10) / 10;
    return { x: round(x + frameOffset.x), y: round(y + frameOffset.y), width: round(width), height: round(height) };
  }

  function describe(el, state) {
    const reason = blocker(el) ?? (wanted > 0 && context && context.state !== 'running' ? 'blocked' : null);
    const viewport = window === window.top ? { width: innerWidth, height: innerHeight } : topViewport;
    return {
      id: state.id,
      kind: el.tagName === 'VIDEO' ? 'video' : 'audio',
      playing: !el.paused && !el.ended,
      audible: !el.muted && el.volume > 0 && hasAudio(el),
      seeks: state.seeks,
      rect: pictureRect(el),
      viewport,
      dpr: Math.round(devicePixelRatio * 1000) / 1000,
      visible: document.visibilityState === 'visible',
      fullscreen: document.fullscreenElement != null,
      videoWidth: el.videoWidth || 0,
      videoHeight: el.videoHeight || 0,
      delayable: reason === null,
      reason,
      delay: state.applied,
      latency: context ? Math.round(((context.baseLatency || 0) + (context.outputLatency || 0)) * 1000) : null,
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
      if (!appConnected) unfollow();
      lastSent = '';
      send();
    } else if (m?.type === 'follow') {
      follow(m.id);
    } else if (m?.type === 'clock') {
      onClock(m);
    } else if (m?.type === 'toggle') {
      for (const [el, state] of media) {
        if (state.id !== m.id) continue;
        if (el.paused) el.play().catch(() => {});
        else el.pause();
      }
    } else if (m?.type === 'seek') {
      for (const [el, state] of media) {
        if (state.id !== m.id || !Number.isFinite(el.duration)) continue;
        const to = Number.isFinite(m.to) ? m.to : el.currentTime + m.by;
        el.currentTime = Math.min(Math.max(0, to), el.duration);
      }
    } else if (m?.type === 'mute') {
      for (const [el, state] of media) if (state.id === m.id) el.muted = !el.muted;
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

  // ---- The followed video (protocol 2) ---------------------------------------------------
  // Only the video the app follows reports more than its place, which keeps the traffic to a
  // few kB/s: each presented frame (requestVideoFrameCallback: its count, its time in the video
  // and when it is shown, mapped to the app's clock), its state at once on every change (and
  // twice a second, so the app knows the page data is fresh), and the boxes over its picture.

  function post(message) {
    if (!port || !appConnected) return;
    try {
      port.postMessage(message);
    } catch {
      port = null;
    }
  }

  function follow(id) {
    let el = null;
    for (const [e, state] of media) if (state.id === id) el = e;
    if (el && followed?.el === el) return;
    unfollow();
    if (!el || el.tagName !== 'VIDEO' || typeof el.requestVideoFrameCallback !== 'function') return;
    const f = { el, id, handle: 0, timer: 0, clock: 0, state: '', overlays: '', ticks: 0 };
    followed = f;
    const onFrame = (now, meta) => {
      if (followed !== f) return;
      f.handle = el.requestVideoFrameCallback(onFrame);
      if (!Number.isFinite(theta)) return;   // not on the app's clock yet
      post({
        type: 'frame', id, presented: meta.presentedFrames, media: meta.mediaTime,
        display: meta.expectedDisplayTime + theta, width: meta.width, height: meta.height,
      });
    };
    f.handle = el.requestVideoFrameCallback(onFrame);
    f.timer = setInterval(() => {
      probeOverlays();
      if (++f.ticks % 2 === 0) sendState(true);
    }, OVERLAY_MS);
    clockSamples = [];
    theta = clockError = NaN;
    for (let i = 0; i < CLOCK_SAMPLES; i++) setTimeout(pingClock, i * 60);
    f.clock = setInterval(pingClock, CLOCK_MS);
    sendState(true);
  }

  function unfollow() {
    if (!followed) return;
    followed.el.cancelVideoFrameCallback?.(followed.handle);
    clearInterval(followed.timer);
    clearInterval(followed.clock);
    followed = null;
  }

  function sendState(force) {
    const f = followed;
    if (!f) return;
    const el = f.el;
    const message = {
      type: 'state', id: f.id, paused: el.paused || el.ended, seeking: el.seeking, seeks: media.get(el)?.seeks ?? 0,
      rate: el.playbackRate, clockError: Number.isFinite(clockError) ? Math.round(clockError * 100) / 100 : null,
      // For the app's theater controls: where the video is, how long it is (null when live), muted.
      time: Math.round(el.currentTime * 10) / 10,
      duration: Number.isFinite(el.duration) ? Math.round(el.duration * 10) / 10 : null,
      muted: el.muted,
    };
    const text = JSON.stringify(message);
    if (!force && text === f.state) return;
    f.state = text;
    post(message);
  }

  // NTP-style: θ = ((t1 − t0) + (t2 − t3)) / 2 is off by at most δ/2, δ = (t3 − t0) − (t2 − t1);
  // the sample with the smallest round trip of the last few counts.
  function pingClock() {
    if (followed) post({ type: 'clock', t0: performance.now() });
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

  /** The picture's box in this frame's viewport (CSS px), or null. */
  function localPicture(el) {
    const r = pictureRect(el);
    if (!r || !frameOffset) return null;
    return { x: r.x - frameOffset.x, y: r.y - frameOffset.y, width: r.width, height: r.height };
  }

  /** Effective opacity (the element's and every ancestor's), cached for one probe. */
  function opacity(node, cache) {
    if (!(node instanceof Element)) return 1;
    if (cache.has(node)) return cache.get(node);
    const style = getComputedStyle(node);
    let value = style.display === 'none' || style.visibility === 'hidden' ? 0 : Number(style.opacity);
    if (value > 0) value *= opacity(node.parentElement ?? node.getRootNode()?.host, cache);
    cache.set(node, value);
    return value;
  }

  function alpha(color) {
    const m = /rgba?\(([^)]+)\)/.exec(color);
    if (!m) return color === 'transparent' ? 0 : 1;
    const parts = m[1].split(/[ ,/]+/).filter(Boolean);
    return parts.length >= 4 ? Number(parts[3]) : 1;
  }

  /**
   * Whether a hit above the video draws anything: text, an image, SVG or canvas, or a background.
   * Players lay transparent layers over the whole video to catch clicks; those do not count.
   */
  function drawsSomething(node, cache) {
    if (opacity(node, cache) < 0.05) return false;
    if (node instanceof SVGElement || /^(IMG|CANVAS|PICTURE|VIDEO|IFRAME)$/.test(node.tagName)) return true;
    const style = getComputedStyle(node);
    if (style.backgroundImage !== 'none' || alpha(style.backgroundColor) > 0.05) return true;
    for (const child of node.childNodes) if (child.nodeType === Node.TEXT_NODE && child.textContent.trim()) return true;
    return false;
  }

  function probeOverlays() {
    const f = followed;
    if (!f) return;
    const el = f.el;
    const pic = localPicture(el);
    const boxes = [];
    if (pic && pic.width >= 16 && pic.height >= 16) {
      const root = typeof el.getRootNode().elementsFromPoint === 'function' ? el.getRootNode() : document;
      const found = new Map();
      const cache = new Map();
      for (let gy = 0; gy < OVERLAY_GRID; gy++) {
        for (let gx = 0; gx < OVERLAY_GRID; gx++) {
          const x = pic.x + (gx + 0.5) * pic.width / OVERLAY_GRID, y = pic.y + (gy + 0.5) * pic.height / OVERLAY_GRID;
          if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) continue;
          for (const hit of root.elementsFromPoint(x, y)) {
            // Everything above the video, down to it; its own ancestors are below it.
            if (hit === el || hit.contains(el)) break;
            if (found.has(hit)) break;
            if (!drawsSomething(hit, cache)) continue;
            found.set(hit, hit.closest(CAPTION_SELECTORS) ? 'caption' : hit.closest(CONTROL_SELECTORS) ? 'control' : 'other');
            break;
          }
        }
      }
      const area = pic.width * pic.height;
      for (const [node, kind] of found) {
        const r = node.getBoundingClientRect();
        const x0 = Math.max(r.left, pic.x), y0 = Math.max(r.top, pic.y);
        const x1 = Math.min(r.right, pic.x + pic.width), y1 = Math.min(r.bottom, pic.y + pic.height);
        const w = x1 - x0, h = y1 - y0;
        // A box over most of the picture is a layer, not an overlay.
        if (w < 2 || h < 2 || w * h > 0.5 * area) continue;
        boxes.push({ x: x0, y: y0, width: w, height: h, kind });
      }
      // Caption containers usually take no clicks (pointer-events: none), so the probe above
      // passes through them: look for them in the player itself, the video's largest ancestor
      // not much bigger than its picture.
      let player = el;
      for (let up = el.parentElement, k = 0; up && k < 8; up = up.parentElement, k++) {
        const r = up.getBoundingClientRect();
        if (r.width * r.height > 1.6 * area) break;
        player = up;
      }
      const captions = player === el ? [] : [...player.querySelectorAll(CAPTION_SELECTORS)];
      for (const node of captions) {
        if (captions.some(other => other !== node && other.contains(node))) continue;
        if (!node.textContent.trim() || opacity(node, cache) < 0.05) continue;
        const r = node.getBoundingClientRect();
        const x0 = Math.max(r.left, pic.x), y0 = Math.max(r.top, pic.y);
        const x1 = Math.min(r.right, pic.x + pic.width), y1 = Math.min(r.bottom, pic.y + pic.height);
        if (x1 - x0 < 2 || y1 - y0 < 2 || (x1 - x0) * (y1 - y0) > 0.5 * area) continue;
        if (boxes.some(b => b.kind === 'caption' && Math.abs(b.x - x0) < 1 && Math.abs(b.y - y0) < 1)) continue;
        boxes.push({ x: x0, y: y0, width: x1 - x0, height: y1 - y0, kind: 'caption' });
      }
      // Native <track> captions: drawn by the browser at the bottom centre, about 5 % of the
      // picture's height a line.
      let lines = 0;
      for (const track of el.textTracks) {
        if (track.mode !== 'showing' || !track.activeCues) continue;
        for (const cue of track.activeCues) lines += String(cue.text ?? '').split('\n').length;
      }
      if (lines > 0) {
        const h = Math.min(lines * pic.height * 0.065, pic.height * 0.4), w = pic.width * 0.7;
        boxes.push({ x: pic.x + (pic.width - w) / 2, y: pic.y + pic.height * 0.95 - h, width: w, height: h, kind: 'caption' });
      }
    }
    const round = v => Math.round(v);
    const list = boxes.map(b => ({
      x: round(b.x + frameOffset.x), y: round(b.y + frameOffset.y), width: round(b.width), height: round(b.height), kind: b.kind,
    }));
    const text = JSON.stringify(list);
    if (text === f.overlays) return;
    f.overlays = text;
    post({ type: 'overlays', id: f.id, boxes: list });
  }

  // ---- Where this frame is in the top frame ----------------------------------------------
  // A frame cannot see its own position in its parent, so it asks; the parent's script finds
  // the <iframe> the question came from and answers with its content box, in top-frame px.

  window.addEventListener('message', e => {
    const d = e.data;
    if (!d || typeof d !== 'object' || !(TAG in d)) return;
    if (d[TAG] === 'where') {
      childAsked = performance.now();
      if (!frameOffset || !e.source) return;
      for (const frame of document.querySelectorAll('iframe, frame')) {
        if (frame.contentWindow !== e.source) continue;
        const box = frame.getBoundingClientRect();
        const style = getComputedStyle(frame);
        e.source.postMessage({
          [TAG]: 'here',
          x: frameOffset.x + box.left + frame.clientLeft + parseFloat(style.paddingLeft),
          y: frameOffset.y + box.top + frame.clientTop + parseFloat(style.paddingTop),
          viewport: window === window.top ? { width: innerWidth, height: innerHeight } : topViewport,
        }, '*');
        return;
      }
    } else if (d[TAG] === 'here' && e.source === window.parent) {
      frameOffset = { x: Number(d.x) || 0, y: Number(d.y) || 0 };
      topViewport = d.viewport ?? null;
    }
  });

  /** Frames with media ask, and so do frames between them and the top, to answer them. */
  function askWhere() {
    if (window === window.top) return;
    if (media.size > 0 || performance.now() - childAsked < 3000) window.parent.postMessage({ [TAG]: 'where' }, '*');
  }

  // ---- Heartbeat -------------------------------------------------------------------------

  scan();
  setInterval(() => {
    scan();
    askWhere();
    send();
  }, REPORT_MS);
})();
