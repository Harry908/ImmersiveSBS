// ImmersiveSBS link: the service worker. It relays between the page script in every frame
// (content.js, one port each) and the ImmersiveSBS app on this PC: a WebSocket to 127.0.0.1,
// which the app accepts only from this extension's origin. Nothing outlives the app: when the
// connection goes, every page is told to stop delaying, so a closed or crashed app never leaves
// the sound late.
'use strict';

const APP_URL = 'ws://127.0.0.1:47391/immersivesbs';
const PROTOCOL = 1;
const MAX_DELAY_SECONDS = 10;

/** The open connection to the app, or null. */
let socket = null;
let connecting = false;
let retryTimer = 0;
let retryMs = 1000;
/** What the app asked for, seconds; 0 while it is not connected. */
let delaySeconds = 0;
/** Frames with media: key "tab:frame:document" -> { port, tab, frame, media: [] }. */
const frames = new Map();
let reportTimer = 0;
let lastReport = '';

function browserName() {
  const brands = navigator.userAgentData?.brands ?? [];
  const named = brands.find(b => !/not.?a.?brand|chromium/i.test(b.brand)) ?? brands.find(b => /chromium/i.test(b.brand));
  return named ? `${named.brand} ${named.version}` : navigator.userAgent;
}

function connect() {
  if (socket || connecting) return;
  clearTimeout(retryTimer);
  connecting = true;
  let ws;
  try {
    ws = new WebSocket(APP_URL);
  } catch {
    connecting = false;
    scheduleRetry();
    return;
  }
  ws.onopen = () => {
    connecting = false;
    socket = ws;
    retryMs = 1000;
    send({ type: 'hello', protocol: PROTOCOL, version: chrome.runtime.getManifest().version, browser: browserName() });
    lastReport = '';
    tellFrames();
  };
  ws.onmessage = e => onAppMessage(e.data);
  ws.onclose = () => {
    connecting = false;
    if (socket === ws) {
      socket = null;
      setDelay(0);
      tellFrames();
    }
    scheduleRetry();
  };
  ws.onerror = () => {}; // onclose follows
}

/** While some frame has media, keep trying to reach the app (it may start later); else wait. */
function scheduleRetry() {
  clearTimeout(retryTimer);
  if (frames.size === 0) return;
  retryTimer = setTimeout(connect, retryMs);
  retryMs = Math.min(retryMs * 2, 10000);
}

function send(message) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

function onAppMessage(text) {
  let m;
  try {
    m = JSON.parse(text);
  } catch {
    return;
  }
  switch (m?.type) {
    case 'ping':
      send({ type: 'pong' });
      break;
    case 'delay':
      setDelay(Number(m.seconds));
      break;
    case 'toggle': {
      // "tab:frame:document#n": the frame's own media number after the '#'.
      const at = String(m.id ?? '').lastIndexOf('#');
      const entry = at > 0 ? frames.get(String(m.id).slice(0, at)) : undefined;
      entry?.port.postMessage({ type: 'toggle', id: Number(String(m.id).slice(at + 1)) });
      break;
    }
  }
}

/** Frames measure and report their media only while the app is there to use it. */
function tellFrames() {
  for (const entry of frames.values()) entry.port.postMessage({ type: 'app', connected: socket !== null });
}

function setDelay(seconds) {
  seconds = Number.isFinite(seconds) ? Math.min(Math.max(seconds, 0), MAX_DELAY_SECONDS) : 0;
  if (seconds === delaySeconds) return;
  delaySeconds = seconds;
  for (const entry of frames.values()) entry.port.postMessage({ type: 'delay', seconds });
}

/** Sends every frame's media to the app, at most ten times a second and only when changed. */
function scheduleReport() {
  if (!reportTimer) reportTimer = setTimeout(report, 100);
}

function report() {
  clearTimeout(reportTimer);
  reportTimer = 0;
  if (!socket) return;
  const media = [];
  for (const [key, entry] of frames)
    for (const m of entry.media) media.push({ ...m, id: `${key}#${m.id}`, tab: entry.tab, frame: entry.frame });
  const text = JSON.stringify({ type: 'media', media });
  if (text === lastReport) return;
  lastReport = text;
  socket.send(text);
}

chrome.runtime.onConnect.addListener(port => {
  const sender = port.sender;
  if (port.name !== 'immersivesbs-frame' || !sender?.tab) return;
  const key = `${sender.tab.id}:${sender.frameId ?? 0}:${sender.documentId ?? ''}`;
  const entry = { port, tab: sender.tab.id, frame: sender.frameId ?? 0, media: [] };
  frames.set(key, entry);
  port.onMessage.addListener(m => {
    if (m?.type !== 'media' || !Array.isArray(m.media)) return;
    entry.media = m.media;
    scheduleReport();
  });
  port.onDisconnect.addListener(() => {
    if (frames.get(key) === entry) frames.delete(key);
    scheduleReport();
  });
  port.postMessage({ type: 'delay', seconds: delaySeconds });
  port.postMessage({ type: 'app', connected: socket !== null });
  connect();
});

// A worker that was stopped while idle comes back on this, and on any frame connecting.
chrome.alarms.create('reconnect', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(() => {
  if (frames.size > 0) connect();
});
