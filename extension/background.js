// ImmersiveSBS link: the service worker. It relays between the page script in every frame
// (content.js, one port each) and the ImmersiveSBS app on this PC: a WebSocket to 127.0.0.1,
// which the app accepts only from this extension's origin. Nothing outlives the app: when the
// connection goes, every page is told to stop delaying, so a closed or crashed app never leaves
// the sound late.
//
// Protocol 2: the app follows one video; the frame holding it reports each presented frame, its
// state and the boxes over its picture, and syncs a clock with the app. Those messages are
// relayed at once, not with the throttled media report.
'use strict';

const APP_URL = 'ws://127.0.0.1:47391/immersivesbs';
const PROTOCOL = 2;
const MAX_DELAY_SECONDS = 10;

/** The open connection to the app, or null. */
let socket = null;
let connecting = false;
let retryTimer = 0;
let retryMs = 1000;
/** What the app asked for, seconds; 0 while it is not connected. */
let delaySeconds = 0;
/** The tab whose pages play late (the followed video's), or null for all (an app before 1.2.1). */
let delayTab = null;
/** Frames with media: key "tab:frame:document" -> { port, tab, frame, media: [] }. */
const frames = new Map();
/** The media id the app follows ("tab:frame:document#n"), or null. */
let followed = null;
let reportTimer = 0;
let lastReport = '';
/** The tab whose toolbar badge says "3D", or null. */
let badgeTab = null;

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
    showStatus();
  };
  ws.onmessage = e => onAppMessage(e.data);
  ws.onclose = () => {
    connecting = false;
    if (socket === ws) {
      socket = null;
      setDelay(0);
      setFollowed(null);
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
      setDelay(Number(m.seconds), Number.isInteger(m.tab) ? m.tab : null);
      break;
    case 'toggle': {
      const target = split(m.id);
      target?.entry.port.postMessage({ type: 'toggle', id: target.n });
      break;
    }
    case 'seek': {
      // By seconds from where it is, or to a time.
      const target = split(m.id);
      const by = Number(m.by), to = Number(m.to);
      if (Number.isFinite(to)) target?.entry.port.postMessage({ type: 'seek', id: target.n, to });
      else if (Number.isFinite(by)) target?.entry.port.postMessage({ type: 'seek', id: target.n, by });
      break;
    }
    case 'mute': {
      const target = split(m.id);
      target?.entry.port.postMessage({ type: 'mute', id: target.n });
      break;
    }
    case 'follow':
      setFollowed(typeof m.id === 'string' ? m.id : null);
      break;
    case 'clock':
      // The app's answer to a frame's clock ping: back to that frame.
      frames.get(String(m.key ?? ''))?.port.postMessage({ type: 'clock', t0: m.t0, t1: m.t1, t2: m.t2 });
      break;
  }
}

/** "tab:frame:document#n" → the frame's entry and its own media number after the '#'. */
function split(id) {
  const text = String(id ?? '');
  const at = text.lastIndexOf('#');
  const entry = at > 0 ? frames.get(text.slice(0, at)) : undefined;
  return entry ? { key: text.slice(0, at), entry, n: Number(text.slice(at + 1)) } : null;
}

/** Tells the frame holding the followed video to report it, and every other frame to stop. */
function setFollowed(id) {
  followed = id;
  const target = id ? split(id) : null;
  for (const [key, entry] of frames) entry.port.postMessage({ type: 'follow', id: target && key === target.key ? target.n : null });
  showStatus();
}

/**
 * The toolbar button tells what the extension is doing: "off" (red) while the app is not
 * running, "on" (grey) while it is connected, and "3D" (green) on the tab whose video the app
 * follows. Hovering it says more, with the sound's delay.
 */
function showStatus() {
  const connected = socket !== null;
  const title = connected ? 'ImmersiveSBS link: connected to the app' : 'ImmersiveSBS link: the app is not running';
  const tab = connected && followed ? split(followed)?.entry.tab ?? null : null;
  try {
    chrome.action.setBadgeText({ text: connected ? 'on' : 'off' });
    chrome.action.setBadgeBackgroundColor({ color: connected ? '#5f6368' : '#b3261e' });
    chrome.action.setTitle({ title });
    if (badgeTab !== null && badgeTab !== tab) {
      // Back to the global badge.
      chrome.action.setBadgeText({ tabId: badgeTab, text: null }).catch(() => {});
      chrome.action.setBadgeBackgroundColor({ tabId: badgeTab, color: connected ? '#5f6368' : '#b3261e' }).catch(() => {});
      chrome.action.setTitle({ tabId: badgeTab, title }).catch(() => {});
    }
    badgeTab = tab;
    if (tab !== null) {
      const sound = delaySeconds > 0 ? `; its sound plays ${Math.round(delaySeconds * 1000)} ms late` : '';
      chrome.action.setBadgeText({ tabId: tab, text: '3D' }).catch(() => {});
      chrome.action.setBadgeBackgroundColor({ tabId: tab, color: '#188038' }).catch(() => {});
      chrome.action.setTitle({ tabId: tab, title: `ImmersiveSBS link: the app follows the video in this tab${sound}` }).catch(() => {});
    }
  } catch {
    // An older browser without the action API: no badge.
  }
}

/** Frames measure and report their media only while the app is there to use it. */
function tellFrames() {
  for (const entry of frames.values()) entry.port.postMessage({ type: 'app', connected: socket !== null });
}

/**
 * Plays the sound of <tab>'s pages <seconds> late (all tabs when tab is null) and every other
 * tab's live: only the followed video's tab waits for its delayed picture, music elsewhere does not.
 */
function setDelay(seconds, tab = null) {
  seconds = Number.isFinite(seconds) ? Math.min(Math.max(seconds, 0), MAX_DELAY_SECONDS) : 0;
  if (seconds === 0) tab = null;
  if (seconds === delaySeconds && tab === delayTab) return;
  delaySeconds = seconds;
  delayTab = tab;
  for (const entry of frames.values()) entry.port.postMessage({ type: 'delay', seconds: frameDelay(entry) });
  showStatus();
}

/** The delay for one frame's pages: the asked one in the delayed tab, else none. */
function frameDelay(entry) {
  return delayTab === null || entry.tab === delayTab ? delaySeconds : 0;
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
    switch (m?.type) {
      case 'media':
        if (!Array.isArray(m.media)) return;
        entry.media = m.media;
        scheduleReport();
        break;
      case 'frame':
      case 'state':
      case 'overlays':
        // The followed video: at once, with its full id.
        send({ ...m, id: `${key}#${m.id}` });
        break;
      case 'clock':
        send({ type: 'clock', key, t0: m.t0 });
        break;
    }
  });
  port.onDisconnect.addListener(() => {
    if (frames.get(key) === entry) frames.delete(key);
    scheduleReport();
  });
  port.postMessage({ type: 'delay', seconds: frameDelay(entry) });
  port.postMessage({ type: 'app', connected: socket !== null });
  if (followed?.startsWith(`${key}#`)) setFollowed(followed);
  connect();
});

showStatus();

// A worker that was stopped while idle comes back on this, and on any frame connecting.
chrome.alarms.create('reconnect', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(() => {
  if (frames.size > 0) connect();
});
