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

## Popup placement rulings (user, confirmed live)

- A popup opened from a taskbar button is centred on that button, its bar-side edge tucked 2 px
  (scaled) under the taskbar so there is one trim line. TaskbarLocator reads the clicked bar's
  window rect at click time (revealed even with auto-hide) and hit-tests the button with UI
  Automation on the pipe thread (150 ms cap, cursor fallback). The tucked strip is cut out of the
  window (Region), and the z-order tuck is re-applied after Activate().
- Auto-hide taskbar: holding the bar up while the popup is open has no supported API. "Popup
  follows the bar" was tried and rejected live (hovering the popup let the bar hide and the popup
  dropped with it). Final: the popup stays where it opened.

## Open items

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
