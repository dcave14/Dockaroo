# NEXT

State as of 2026-10-16:
- Dockaroo: a resident tray app (WinForms, .NET 9) that gives each group of shortcuts its own
  taskbar button; clicking the button opens a popup of the group's apps. HARD CONSTRAINT: speed is
  as important as usefulness (popup show path zero IO, no per-click process spawn).
- Implemented: data model + config store, icon pipeline, popup + launch, per-group .lnk with
  AppUserModelID, legacy-app config import, editor (sidebar, cards, taskbar preview, Appearance
  page, dirty guard), five logo palettes x light/dark, PerMonitorV2 DPI, popup centred on its
  taskbar button and tucked 2 px under the bar.
- Installed on this machine in `E:\SOFTWARE\Dockaroo` (BUILD.md "Install and update"). Data in
  `%LOCALAPPDATA%\Dockaroo`. The user's taskbar pins (Browsers, DEV, GAMING, MUSICA, PC STUFF) run
  it; EDITING has a shortcut in the data root's Shortcuts folder but is not pinned.

Rules: TESTING.md (never drive the user's desktop; test windows behind the user's apps, muted).
Commit only when the user asks; never push.

Start here in a fresh session: CLAUDE.md (agent guide), then this file.

Retiring the old setup (old workspace, previous install and data, the 2021 legacy app): checklist
in RETIRE-OLD-SETUP.md - local and gitignored on purpose, so the old names never enter history.
Delete that file once the old folders are gone.

Next (user to pick order):
1. Palette logo as the default .lnk icon for groups without a custom icon (design spec
   "defaultGroupIcon"; pinned links cache icons, so re-pin to see it).
2. Cleanup task (due): P3-2 ShellInterop dedup, P3-4 comment trim, P3-5 scratch data, deferred
   notes below, GroupStore.cs split.
3. Remaining P2 (docs/BACKLOG.md): P2-1, P2-2, P2-3, P2-7, P2-8, P2-9, P2-10.
4. Editor follow-ups: grip-drag autoscroll; UIA child objects for owner-drawn lists.

Decisions open for the user: whether design/ and ico/ are committed to the repo.

## Logo rulings (user)

- 2026-10-16: the cyborg kangaroo (`ico/dockaroo/Dockaroo-*.svg`, one geometry, colours only
  differ) replaces every palette logo: amber -> Original, ocean, coral, graphite-gold, and a mint
  variant derived from the ocean SVG (`ico/dockaroo/Dockaroo-mint.*`). `app-icon.ico` stays the
  mint logo (mint is the default palette; ThemeServiceTests pins it).
- 2026-10-16: "logo too small, blurry in the app". The shipped ICOs are re-rendered from those
  SVGs with a square viewBox tight around the art (2% margin; art 96% of the frame height, was
  90%), so they no longer byte-match `ico/dockaroo`. Editor logos are drawn from
  `PaletteLogos` (256 frame, trimmed, bicubic to the exact pixel size) instead of stretched ICO
  frames; the sidebar logo is 60 px tall (44 compact), was a 44 px square.
- 2026-10-16: the "Dockaroo" brand text is centred on the logo's middle. Its letters' ink is
  centred on the kangaroo's head-to-feet middle (`PaletteLogos.VisualCentre`, 0.565 of the art
  height): the thin dark ears barely show on the sidebar, so the full art box reads as too tall.
  Deferred review nit: the `Dockaroo.App.Palettes.logo-` prefix is repeated in ThemeService,
  PaletteLogos and StaticThemeSource.

## Popup transparency ruling (user)

- 2026-10-16: the group's transparency fades only the popup's surface, never the icons. The
  popup is a per-pixel-alpha layered window (`PopupForm.Layered.cs`, UpdateLayeredWindow)
  instead of `Form.Opacity`; icons, placeholder letters and the hover plate stay opaque. The
  editor's taskbar preview draws the same frame. Not yet seen live on the real taskbar.

## Popup placement rulings (user, confirmed live)

- 2026-10-16: the popup keeps its bottom trim when an auto-hide bar drops (it looked cut off).
  While the bar covers the tucked strip it stays clipped (one trim line); once the bar slides
  away the clip is dropped and the popup's own trim shows. The popup still never moves.
- 2026-10-16: double-clicking Dockaroo.exe opens the editor (payload `editor`); pin clicks still
  open their group's popup.
- A popup opened from a taskbar button is centred on that button, its bar-side edge tucked 2 px
  (scaled) under the taskbar so there is one trim line. TaskbarLocator reads the clicked bar's
  window rect at click time (revealed even with auto-hide) and hit-tests the button with UI
  Automation on the pipe thread (150 ms cap, cursor fallback). The tucked strip is cut out of the
  window (Region), and the z-order tuck is re-applied after Activate().
- Auto-hide taskbar: holding the bar up while the popup is open has no supported API. "Popup
  follows the bar" was tried and rejected live (hovering the popup let the bar hide and the popup
  dropped with it). Final: the popup stays where it opened.

## Open items

- ROOT CAUSE of the 2026-10-16 switch-over mess (missing groups, blank pin icons, refused drag
  chip): the agent's shell runs in the Claude desktop app's MSIX sandbox, so the data root it
  created (`%LOCALAPPDATA%\Dockaroo`) went to the sandbox's private copy. Dockaroo started by the
  user or by a pin saw an empty data root; pinned .lnk icon paths pointed at a folder Explorer
  could not see. Fix = the user copies the sandbox copy to the real folder themselves (see
  CLAUDE.md "SANDBOX TRAP"). Several entries below were diagnosed before this was known: the
  "hung startup" and "invisible editor" items may be partly the same cause; re-check them once
  the real data root exists.
- CAUSE FOUND (2026-10-16): "Edit groups..." did nothing because the agent had restarted the
  resident with `Start-Process -WindowStyle Hidden`. Windows applies the launcher's STARTUPINFO
  show state (SW_HIDE) to the process's first ShowWindow, so the editor's first Show() stayed
  hidden (Visible=True, no WS_VISIBLE). Pin clicks and normal starts are not affected. Hardening
  (optional): in TrayContext.OpenEditor, if the editor is not IsWindowVisible after Show(), call
  ShowWindow(SW_SHOWNORMAL) once; and re-Show an existing editor that is not visible instead of
  only Activate()-ing it.
- BUG (2026-10-16, user hit it): a resident started while Explorer was busy (right after an
  icon-cache refresh) hung inside TrayContext.Preload - its UI thread sat in a pumped COM wait, so
  the tray kept the constructor's zero-group menu ("Show popup" / "Edit groups..." greyed out) and
  pin clicks queued in StartupPopupQueue forever. Replaying the same steps later took ~2 s, so it
  is an out-of-process shell call that never returned. Suspects: PinnedStateCache.Refresh ->
  IPinnedList3.IsPinned (served by explorer) and shell icon extraction. Fix: build the menu from
  LoadAll before any shell call, move the pin probe off the startup path (or bound it with a
  timeout, falling back to the pinned-folder scan), replay queued requests without waiting on it;
  tests with a hanging probe hook. Workaround: quit and restart Dockaroo.
- LOGO-1 (2026-10-16): FIXED - "Your current look" ate the `&` of "Graphite & gold"
  (`_lookSummary` now has UseMnemonic = false). Still open: Original palette swatches/accents are
  azure-amber-emerald while its logo is now
  amber; ico/dockaroo README/manifest still describe the old D set; themes.json `stem`/`assets`
  keys point at old files (unread by code).
- UI-1 (2026-10-16): in dark mode the editor's shortcut list shows a bright white scrollbar
  (visible with 7 shortcuts in DEV at 1440x960). The list is owner-drawn; theme its scrollbar
  like the others (SetWindowTheme "DarkMode_Explorer") or draw its own.
- Human checks not automatable under TESTING.md: a failing click shows ONE tray balloon; clicking
  a pinned button of a renamed/deleted group shows the "no longer exists" balloon; Win+N on a
  pinned group opens the popup without launching entry N; editor Change icon on a pinned group
  (re-pin to see it); Appearance palette switch updates tray icon, editor and logo; switching
  groups with unsaved edits shows Save / Don't save / Cancel.
- Editor on a real non-100% monitor not verified (the user's monitors are both 100%).
- Deferred review notes: GroupStore cache entry can be stomped by a concurrent load from disk;
  "New group..." with an editor open only focuses it; StartupPopupQueue Post-vs-Complete ordering
  and a throwing show callback mid-replay dropping the queue tail; SingleInstance lacks an
  ObjectDisposedException catch (shutdown-only race); GroupLnkWriter.DefaultOutputDirectory() is
  unused; PreloadDelayHook is unused by tests; TrayContext cold-request comment overstates
  (duplicates are dropped, not joined); corrupt-config rejection in CheckGroupExists has no
  dedicated test; icon cache GetOrLoad(forceReload) on a disposed cache throws late; a directory
  named "foo.png" classifies as Image.
- DataRootResolver.MigrateLegacyData gate is "root has no groups": deleting every group and
  restarting copies exe-adjacent legacy groups again, and it copies exe-adjacent Shortcuts with
  stale targets. Low impact (only portable-to-installed upgrades); fix with a marker file.
- Env flake while the user's resident app runs: SingleInstanceServerTests.OccupiedPipeName ("All
  pipe instances are busy") - rerun with the app closed.
- Env flake: TaskbarLocatorTests.ElementRectAt_TheComBindingAnswers asks UI Automation for the
  live element at the primary screen centre; it failed once in a full run (2026-10-16) and passed
  on rerun - whatever the user has there can move. Make it hit-test a test-owned window instead.
- MS Store icons work without Windows.winmd (shell:AppsFolder + IShellItemImageFactory).
- Real taskbar pinning (drag the .lnk onto the taskbar) is a manual user step; silent pinning is
  impossible on Windows 11 25H2, so the editor hands off a drag chip.
- .NET 10 SDK not installed (SDKs: 5.0.415, 9.0.317). TFM is net9.0-windows; the bump is one line
  per csproj once the SDK is installed. Ask the user before installing.
