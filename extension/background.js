// ImmersiveSBS link: the service worker. It relays between the page script in every frame
// (content.js, one port each) and the ImmersiveSBS app on this PC: a WebSocket to 127.0.0.1,
// which the app accepts only from this extension's origin. Nothing outlives the app: when the
// connection goes, every page is told to stop delaying, so a closed or crashed app never leaves
// the sound late.
//
// Protocol 3 (the extension ships with the app; the app refuses any other): the media report for
// the sound, the theater's reports (its picture, frames, state, boxes and clock pings, relayed
// at once), the app's status (glasses in 3D, the button wanted, the 3D panel's values) and the
// 3D panel's commands.
'use strict';

const APP_URL = 'ws://127.0.0.1:47391/immersivesbs';
const PROTOCOL = 3;
const MAX_DELAY_SECONDS = 10;

/** The open connection to the app, or null. */
let socket = null;
let connecting = false;
let retryTimer = 0;
let retryMs = 1000;
/** What the app asked for, seconds; 0 while it is not connected. */
let delaySeconds = 0;
/** The tab whose pages play late (the theater's), or null. */
let delayTab = null;
/** Frames with media: key "tab:frame:document" -> { port, tab, frame, media: [] }. */
const frames = new Map();
/** The open theater: { key (its frame), id (its media, "key#n"), tab }, or null. */
let theater = null;
let reportTimer = 0;
let lastReport = '';
/** The tab whose toolbar badge says "3D", or null. */
let badgeTab = null;
/** The app's last status (glasses in 3D, the theater button wanted, the 3D settings), or null. */
let appStatus = null;
/** Browser windows the theater put in full screen: window id -> the state to go back to. */
const fullscreened = new Map();

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
      appStatus = null;
      setDelay(0);
      tellFrames();
      showStatus();
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
    case 'status':
      // Whether the glasses show 3D, whether to offer the theater button, and the 3D settings.
      appStatus = { type: 'status', glasses: m.glasses === true, offer: m.offer === true, settings: m.settings ?? null };
      for (const entry of frames.values()) entry.port.postMessage(appStatus);
      break;
    case 'theater':
      // The app's tray or shortcut.
      if (m.open === true) openOnActiveTab();
      else if (theater) frames.get(theater.key)?.port.postMessage({ type: 'theater', open: false });
      break;
    case 'clock':
      // The app's answer to a frame's clock ping: back to that frame.
      frames.get(String(m.key ?? ''))?.port.postMessage({ type: 'clock', t0: m.t0, t1: m.t1, t2: m.t2 });
      break;
  }
}

/** Opens the theater on the largest playing video of the tab in front (a paused one if none plays). */
async function openOnActiveTab() {
  let tabId;
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    tabId = tab?.id;
  } catch {
    return;
  }
  let best = null;
  for (const entry of frames.values()) {
    if (entry.tab !== tabId) continue;
    for (const m of entry.media) {
      if (m.kind !== 'video' || !(m.area > 0)) continue;
      const score = (m.playing ? 1e12 : 0) + m.area;
      if (!best || score > best.score) best = { entry, id: m.id, score };
    }
  }
  best?.entry.port.postMessage({ type: 'theater', id: best.id, open: true });
}

/**
 * The toolbar button tells what the extension is doing: "off" (red) while the app is not
 * running, "on" (grey) while it is connected, and "3D" (green) on the tab whose video is in the
 * theater. Hovering it says more, with the sound's delay.
 */
function showStatus() {
  const connected = socket !== null;
  const title = connected ? 'ImmersiveSBS link: connected to the app' : 'ImmersiveSBS link: the app is not running';
  const tab = connected && theater ? theater.tab : null;
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
      chrome.action.setTitle({ tabId: tab, title: `ImmersiveSBS link: this tab's video is in the 3D theater${sound}` }).catch(() => {});
    }
  } catch {
    // An older browser without the action API: no badge.
  }
}

/** Frames measure and report their media only while the app is there to use it. */
function tellFrames() {
  for (const entry of frames.values()) {
    entry.port.postMessage({ type: 'app', connected: socket !== null });
    if (!socket) entry.port.postMessage({ type: 'status', glasses: false, offer: false, settings: null });
  }
}

/**
 * The theater asked by the app (no click in the page, which full screen needs) fills the
 * browser window and puts the window in full screen instead; closed, the window goes back.
 */
async function windowFullscreen(windowId, on) {
  if (!Number.isInteger(windowId) || !chrome.windows) return;
  try {
    if (on) {
      const w = await chrome.windows.get(windowId);
      if (w.state === 'fullscreen') return;
      fullscreened.set(windowId, w.state ?? 'normal');
      await chrome.windows.update(windowId, { state: 'fullscreen' });
    } else if (fullscreened.has(windowId)) {
      const state = fullscreened.get(windowId);
      fullscreened.delete(windowId);
      await chrome.windows.update(windowId, { state });
    }
  } catch {
    // The window is gone.
  }
}

/** Plays the sound of <tab>'s pages <seconds> late and every other tab's live. */
function setDelay(seconds, tab = null) {
  seconds = Number.isFinite(seconds) ? Math.min(Math.max(seconds, 0), MAX_DELAY_SECONDS) : 0;
  if (seconds === 0 || tab === null) {
    seconds = 0;
    tab = null;
  }
  if (seconds === delaySeconds && tab === delayTab) return;
  delaySeconds = seconds;
  delayTab = tab;
  for (const entry of frames.values()) entry.port.postMessage({ type: 'delay', seconds: frameDelay(entry) });
  showStatus();
}

/** The delay for one frame's pages: the asked one in the delayed tab, else none. */
function frameDelay(entry) {
  return entry.tab === delayTab ? delaySeconds : 0;
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

/** The theater opened, moved or closed in a frame: tell the app, with the full id and the tab. */
function onTheater(key, entry, m) {
  const id = `${key}#${m.id}`;
  if (m.open === true) {
    theater = { key, id, tab: entry.tab };
    send({ ...m, id, tab: entry.tab });
  } else if (theater?.id === id) {
    theater = null;
    send({ type: 'theater', id, open: false });
  }
  showStatus();
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
      case 'theater':
        onTheater(key, entry, m);
        break;
      case 'frame':
      case 'state':
      case 'overlays':
        // The theater's video: at once, with its full id.
        send({ ...m, id: `${key}#${m.id}` });
        break;
      case 'clock':
        send({ type: 'clock', key, t0: m.t0 });
        break;
      case 'command':
        // A button of the theater's 3D panel: the app changes the setting and sends its status back.
        if (typeof m.name === 'string') send({ type: 'command', name: m.name });
        break;
      case 'windowFullscreen':
        windowFullscreen(sender.tab.windowId, m.on === true);
        break;
    }
  });
  port.onDisconnect.addListener(() => {
    if (frames.get(key) === entry) frames.delete(key);
    // A page that went away (closed, reloaded, navigated) takes its theater with it.
    if (theater?.key === key) onTheater(key, entry, { id: theater.id.slice(key.length + 1), open: false });
    scheduleReport();
  });
  port.postMessage({ type: 'delay', seconds: frameDelay(entry) });
  port.postMessage({ type: 'app', connected: socket !== null });
  if (appStatus) port.postMessage(appStatus);
  connect();
});

showStatus();

// A worker that was stopped while idle comes back on this, and on any frame connecting.
chrome.alarms.create('reconnect', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(() => {
  if (frames.size > 0) connect();
});
