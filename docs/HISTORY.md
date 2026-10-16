# Dockaroo: how it got here

Dockaroo is a from-scratch rewrite of an old, dormant .NET Framework 4.7.2 taskbar-grouping
utility (last release 2021, MIT). The rewrite was built 2026-10-15/16 under a working name and moved
into this repo on 2026-10-16 with a fresh history; the old workspace and the old app's install were
retired after that. This file keeps the decisions and findings worth knowing.

## Timeline (all 2026-10-15 unless noted)

1. Stack: WinForms on .NET (9 now, 10 when the SDK lands), one resident tray process.
2. Scaffold: resident tray app, single-instance named-pipe handoff (second launch exits in ~65 ms).
3. Data layer: `Group` / `ShortcutEntry` with the legacy validation rules, `GroupStore` (atomic save,
   cache-first, corrupt-safe, copy-on-read/write, per-name save/delete serialisation).
4. Icon pipeline: exe / lnk / folder / image / store-app extraction, memory cache with a zero-IO
   lookup plus a PNG disk cache.
5. Popup + launch: grid, hotkeys 1-9/0 and Ctrl+Enter (AllowOpenAll), anchoring, background cold
   load; zero-IO show path.
6. Per-group .lnk with AppUserModelID and `--create-link` (exit codes 0/1/2, unknown groups
   rejected). A .lnk launch shows the popup in 77-145 ms.
7. Group editor (later redesigned in step 15).
8. Legacy config import: converted the user's real old setup (6 groups, 35 shortcuts), source
   proven untouched by before/after hashes.
9. P0 fixes: shell icons keep alpha (manual DIB read; `Image.FromHbitmap` dropped alpha), the
   resident honours its own args and queues requests during preload, the pipe is scoped per
   session + user (`Dockaroo.<session>.<sid>`, CurrentUserOnly, 500 ms backoff), data root moved
   to `%LOCALAPPDATA%` with portable mode (`portable.txt`).
10. Logo: the "three tiles in a cradle" v2 mark in mint, enlarged to 92% canvas fill; it holds up at
    24 px where the v1 monitor+dock set did not.
11. Popup look: sized to its app count, taskbar replica (#202020 fill, 1 px #424242 trim, 40x40
    r3 plates, 44 px pitch, 24 px icons), icons only - no labels (user: no text under the
    icons). Size = (8 + 44*perRow) x (7 + 44*rows). The popup merges under its pin via AUMID.
12. P1 fixes: named colours save as hex, 4-edge working-area clamp, Win+digit no longer launches,
    Ctrl+Enter on KeyDown, launch failures raise one tray balloon, rename rewrites the .lnk and
    asks to re-pin, editor icons load off the UI thread.
13. PerMonitorV2 DPI: popup in physical pixels per monitor (150% = 60 px plates / 36 px icons),
    closes on a DPI change; editor autoscales.
14. Appearance foundation: `appearance.json` (mode + palette), `themes.json` (5 palettes x
    light/dark, contrast-checked), palette logos, `ThemeService` (System follows Windows live).
15. Editor shell to the design handoff: sidebar, cards, sticky footer, owner-drawn themed controls,
    52 px shortcut rows with grip drag, dirty guard (Save / Don't save / Cancel), responsive layout.
16. Taskbar preview card, Appearance page, group Change icon (multi-size `GroupIcon.ico` via
    `IcoWriter`), popups render at most 90% transparent.
17. Placement rulings and install (see below); switched the user's 5 pins from the old app.
18. 2026-10-16: renamed to Dockaroo (exe, AUMIDs with underscores, mutex, pipe, data root), new
    repo, pins and data moved over.

## User rulings on the popup

- Opened from a taskbar button: centred on that button and flush with the taskbar; then lowered so
  its bar-side edge sits 2 px (scaled) behind the bar - one trim line, not two. Implemented with
  `TaskbarLocator` (bar window rect at click time, UI Automation button hit-test with a 150 ms cap
  on the pipe thread), `PopupGeometry.PlaceOnTaskbar`, a window Region that cuts the tucked strip,
  and a z-order tuck just below the taskbar re-applied after `Activate()`.
- Auto-hide taskbar: the user wanted the bar held up while the popup is open. No supported API
  does that; toggling auto-hide changes the system setting, and pushing the bar back jitters.
  "Popup follows the bar" was built and rejected live. Final: the popup stays where it opened.

## Findings worth remembering

- WinUI 3 was ruled out: trimming breaks WinRT/XAML metadata, unpackaged single-file extracts to
  temp on first launch, slower startup than WinForms.
- MS Store app icons work without `Windows.winmd`: `shell:AppsFolder` + `IShellItemImageFactory`.
- Silent taskbar pinning is impossible on Windows 11 25H2 (build 26200): the "taskbarpin" verb is
  gone; `IPinnedList3.Modify` returns S_OK but does nothing; no other route found. Hence the drag
  chip. `IPinnedList3.IsPinned` (read-only) still works and `PinnedStateCache` uses it.
- PrintWindow: a never-shown window captures all black with `PW_RENDERFULLCONTENT`; use plain
  PrintWindow there. Layered windows may need a message pump and retry before the capture.
- A window placed off-screen registers no taskbar presence; for taskbar checks use on-screen but
  behind (ShowWithoutActivation + HWND_BOTTOM).
- An auto-hidden taskbar's working area equals the screen bounds, so placement must read the bar's
  real window rect rather than the working area.
