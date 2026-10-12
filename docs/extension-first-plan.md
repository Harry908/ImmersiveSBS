# Extension-first plan

The browser extension becomes part of the app: it ships in the app's folder, the two speak one
protocol version, and for browser video the page is the source of truth. Everything the app
does to guess, check or second-guess what the page already knows is removed. What stays is what
only the app can do: capture, depth, the stereo warp, the glasses, the delay buffer.

Written 2026-10-12, after the theater moved into the page (extension 1.3). Phases 1–3 were done
the same day with all three decisions as recommended (extension 1.4.0, protocol 3; progress.md
has the details); phase 4 stays open.

## Decisions to make first

1. **3D for browser video only in the theater (recommended).** Today a browser video can also be
   converted in place, in a normal page with the desktop around it (AI 3D "Video only"). That
   mode is what needs most of the guessing: which window a page is in, where its video is on
   the screen, what the page draws over it, which of several videos to follow. In the theater
   all of that is known: the player is the extension's own, full screen, with its own controls
   and captions. If in-place conversion goes, phase 2 removes about 600 lines of the app and
   about 300 of the extension. Whole screen 3D stays for everything that is not a browser video
   (games, local players).
2. **Already-3D video chosen in the player, not detected (recommended).** `Native3DDetector`
   guesses side-by-side and top-bottom video from the picture; on 2026-10-12 it flipped a 2D video
   to "shown as is" every few seconds, which also cut the delayed sound. A "2D / side-by-side /
   top-bottom" switch in the player's 3D panel is exact and needs no luma grid. Whole screen keeps
   the setting as a manual choice.
3. **The aspect stretch in the player (recommended).** `VideoStretch` widens the region in the
   app's warp. The player can draw its canvas stretched itself; the app then has no stretch.

The phases below assume all three. Phase 1 holds either way.

## What is redundant, and why

| Feature (where) | Why it was built | With the extension part of the app | Action |
|---|---|---|---|
| Compatibility with older extensions: protocol 1, `Protocol < 2` paths, delay without a tab ("before 1.2.1"), missing `window`/`theater` fields (`BrowserLink`, `BrowserMedia`, `background.js`) | Old extension copies in users' browsers | The app ships its extension; a different version is an error to fix, not a mode | Phase 1: one protocol number, checked at hello; mismatch → tray message "reload the extension" |
| `UsePageData` setting (`AppSettings`, `AppController.UpdateBrowserVideo`, Settings window) | Turn off page data if a page reports wrongly | The page is the source of truth; the theater's page is our own player | Phase 1: remove |
| "Needs the browser extension" paths: `BufferAvailable`, greyed timing choices, `LiveSoundMaxSeconds` cap wording, "AI 3D: no video from the browser extension" (`AppController`, `AiStereo`, `Session.Panel`, `TrayIcon`, Settings) | The app worked without the extension | Without it, browser 3D is simply unavailable; one clear message instead of degraded modes | Phase 1: keep only the case the page itself reports (sound that cannot be delayed: protected or cross-origin media, `BrowserCanDelay`) |
| Pick shortcut (`ShortcutCommand.PickVideo`, `PageVideoAt`) | Choose among videos when the app guessed wrong | The theater button is the choice | Phase 1: remove |
| In-place video following: `PageVideosNear`, `PageVideoCandidates`, nine probe points, `FollowBrowserVideo`, `PlayingBrowserVideo`, `VideoInWindow`, foreground-window switching, `BrowserGraceMs`, `browserPickedAt`/hold rules (`AppController`, ~250 lines) | Find which video, in which window, the user means | The followed video is the one in the theater; the page says so | Phase 2: replace with "the media reported `theater`, else none" |
| Page → screen geometry: `BrowserMedia.ScreenRect`/`ToScreen`, the bottom-left-of-client assumption (breaks with Edge's vertical tabs), `WindowError`, `Native.WindowInWorkArea`, `RootWindowAt` checks, the page's `window`/`viewport`/`dpr` report | Place a page's CSS rectangle on the desktop | The theater is always full screen (element or window): its picture rectangle is in screen px from the virtual monitor's corner | Phase 2: map `rect × dpr + monitor origin`; drop the rest |
| The extension's overlay probe: an 8×8 `elementsFromPoint` grid, `drawsSomething`, opacity/alpha checks, caption guessing by class name (`content.js`, ~120 lines) | Learn what an unknown player draws over its video | In the player, the overlays are our own controls and our own caption element | Phase 2: report the player's boxes only (already done in the theater) |
| Iframe position protocol (`where`/`here` messages, `frameOffset`, `topViewport`) | Place a video inside an iframe in the top page | A full-screen player's viewport is the screen, in any frame | Phase 2: remove |
| `follow` message and per-connection following (`BrowserLink.Follow`, `setFollowed`) | Ask one frame among many to report frames and state | The player's frame reports its own video while open | Phase 2: the player starts frame/state reports itself |
| Trust rules for page data: `PausedButChangingSeconds`, `Fresh` fallbacks to the capture (`AiStereo`, improvement-plan §3.5 rules 2–3) | A page could report wrongly or stop reporting | Our player's state is exact; if reports stop, the theater has closed | Phase 2: page state is authoritative in the theater |
| Change detection for video: capture dirty rectangles, luma-grid check, `OnMissedChange`, `HandOverEarly`, `repeatsSkipped` (`AiStereo`, `Session`) | Hand only new video frames to the model | The player reports each presented frame (`requestVideoFrameCallback`): a new page frame is a new picture | Phase 3: hand over on page frames in the theater; Whole screen hands over on new captured frames |
| Luma grid and `Native3DDetector` (+ tests) | Already-3D detection, change detection | Decision 2; change detection as above | Phase 3: remove (unless decision 2 says keep) |
| App theater fit: `UiLayer.Theater`/`TheaterCursor`, the renderer's theater branch, `TheaterGeometry.Fit`/`Map`, `AiStereo.theaterRegion` | Enlarge a video that was small in the page | The player is full screen at the video's own size; letterboxing is the canvas's | Phase 3: remove; the badge stays |
| Stretch: `VideoStretch`, `AiStereo.Stretched`, the "drawn" rectangle through the synth | Wrong aspect ratio | Decision 3: the canvas draws it | Phase 3: remove from the app, add to the player's panel |
| `AiTarget.Video` and its tray/settings entries | 3D for one video in place | Decision 1 | Phase 2: targets become Off / Whole screen; the theater is always 3D |

Not redundant, and unchanged: capture (`DuplicationCapture`, `GraphicsCapture`), displays and
glasses state, `CursorGuard`, `StrayWindowGuard`, the depth pipeline (`DepthService`,
`DepthPostProcessor`, `EdgeFilters`, `CaptionLock` for burned-in subtitles, static overlays for
burned-in logos), `StereoSynth`, `VideoDelayBuffer`, `MatchedDelay`, the page's delayed sound,
the clock sync, the model badge, Whole screen 3D.

## Phases

Each phase builds, passes the tests (tests for removed code go with it), and gets a run in the
glasses before the next starts.

### Phase 1: one extension, one protocol (small, safe)

1. `PROTOCOL = 3` in `background.js`; the app accepts only 3 (`BrowserLink` hello). Mismatch:
   log it, tray message with the reload steps, Settings shows it.
2. Remove the compatibility paths listed above (protocol 1, tab-less delay, optional fields).
3. Remove `UsePageData` and the pick shortcut (settings files keep loading: unknown keys are
   ignored, `PickVideo` drops out of the shortcut map).
4. One "no extension" state: Settings and the tray say browser 3D needs the extension, with the
   folder button; buffer choices stay available when it is connected.

Check: the log's `Browser: … connected (extension 1.3.x, protocol 3)`; an old copy is refused
with the message.

### Phase 2: browser video means the theater (decision 1)

1. The extension reports, besides the media list for the sound, the open player: its picture
   rectangle in screen px, its frames, state, boxes and clock, without a `follow` request.
2. `UpdateBrowserVideo` shrinks to: the theater's media (the focused window's if two are open),
   else none. The region is the reported rectangle on the virtual monitor.
3. Remove in-place following and geometry (the table's rows), the overlay probe and the iframe
   protocol in `content.js`, and `AiTarget.Video` (settings with "Video" load as Off).
4. Keep the sound-delay reports for every media element in the followed tab (`BrowserCanDelay`
   needs them).

Check: open, leave and reopen the theater on YouTube and one other site; switch Edge windows
with a theater open in each; the log shows the theater video followed at once, no "no longer
following" during play.

### Phase 3: the page decides when the picture changes (decisions 2 and 3)

1. In the theater, a frame goes to the model when the page reported a new presented frame since
   the last hand-over; paused means none. Whole screen hands over each new captured frame.
2. Remove the luma grid, dirty-rectangle trust, `Native3DDetector` and their tests; the player's
   panel gets "2D / side-by-side / top-bottom" and an aspect choice, sent to the app as settings.
3. Remove the app's theater fit and stretch.

Check: the `Measures:` line's hand-overs equal the video's frame rate while playing and 0 while
paused; M1 depth age no worse than before; an SBS video shown as is only when chosen.

### Phase 4: packaging

The extension loads unpacked today, which Edge warns about at each start. Options, in order of
effort: keep unpacked (the app checks the version and explains); publish it unlisted on Edge
Add-ons and the Chrome Web Store (the app then links to the listing); or force-install by policy,
which Windows allows only on managed machines. Decide when the protocol settles.

## Risks

- **A covered video may stop presenting frames.** If Edge stops calling
  `requestVideoFrameCallback` for the page's element under the player, phase 3's hand-over loses
  its signal. Measure in the first run after the 1.3 theater (the `Browser:` line's fps while the
  theater is open). Fallback: the player reports its own canvas draws instead.
- **Subtitles not in caption-like boxes** do not reach the player. `CaptionLock` still flattens
  burned-in ones; DOM ones on unusual sites would be missing from the player.
- **Phase 2 removes in-place 3D.** If the user wants it back later, it comes back as a player
  option ("windowed theater"), not as guessing.
