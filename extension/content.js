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
'use strict';

(() => {
  const TAG = '__immersiveSbs';
  const PORT_NAME = 'immersivesbs-frame';
  const REPORT_MS = 500;
  const SHADOW_SCAN_MS = 5000;       // while the app is connected
  const IDLE_SHADOW_SCAN_MS = 30000; // while it is not (a page whose only video is in a shadow root)

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
    for (const type of ['play', 'playing', 'pause', 'ended', 'seeked', 'volumechange', 'loadedmetadata', 'emptied']) {
      el.addEventListener(type, () => {
        const state = media.get(el);
        if (!state) return;
        if (type === 'seeked') state.seeks++;
        if (type === 'playing') queueApply();
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
      lastSent = '';
      send();
    } else if (m?.type === 'toggle') {
      for (const [el, state] of media) {
        if (state.id !== m.id) continue;
        if (el.paused) el.play().catch(() => {});
        else el.pause();
      }
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
