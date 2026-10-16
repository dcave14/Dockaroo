# Dockaroo - agent guide

Read this first, then NEXT.md (status, open items, what to do next) and TESTING.md (hard rules for
anything that runs on this machine). History and the reasons behind past decisions are in
docs/HISTORY.md; the open backlog is docs/BACKLOG.md; build, install and runtime details are in
BUILD.md.

## What it is

A Windows 11 tray app: each group of shortcuts gets its own pinned taskbar button, and clicking it
opens a small taskbar-styled popup of the group's apps, centred on the button. WinForms on .NET 9
(`net9.0-windows`), one resident process; every pin click starts a short-lived second instance
that hands `popup:<x>:<y>:<group>` to the resident over a named pipe and exits.

HARD CONSTRAINT: speed is as important as usefulness. The popup show path does zero file IO
(groups and icons are preloaded; a group created later is loaded on a background thread).

## Layout

- `src/Dockaroo.Core` - model (`Group`, `ShortcutEntry`), `GroupStore` (config/<Name_With_Underscores>/group.xml),
  `DataRootResolver`, icons (`IconExtractor`, `IconCache`, `IcoWriter`, `LnkResolver`), `Linking/GroupLnkWriter`
  (per-group .lnk + AppUserModelID), `Popup/` geometry and DPI metrics, `Appearance/` (themes.json,
  5 palettes x light/dark), `Legacy/LegacyConfigImporter` (imports the old app's config).
- `src/Dockaroo.App` - assembly/exe name `Dockaroo` (`Dockaroo.exe`, `Dockaroo.dll`), namespaces
  `Dockaroo.App.*`. `Program` (single instance, `--create-link`), `TrayContext`, `PopupForm` +
  `PopupPainter`, `TaskbarLocator` (bar rect + UI Automation button hit-test), `PinnedStateCache`,
  `Editor/` (EditorForm partials, sidebar, cards, owner-drawn themed controls, Appearance page,
  taskbar preview), `Appearance/ThemeService`.
- `tests/Dockaroo.Core.Tests`, `tests/Dockaroo.App.Tests` - xUnit. App internals are visible to the
  App tests.
- `tools/PopupCapture`, `tools/EditorCapture` - off-screen PrintWindow capture tools for visual gates
  (not in the solution; see TESTING.md).
- Untracked, the user's: `design/interface-v1/` (the design handoff the editor implements) and
  `ico/` (logo masters, including `ico/dockaroo/` and `ico/cyborg-kangaroo/` concepts). Whether
  they get committed is the user's call.

## Commands

```
dotnet build Dockaroo.sln -c Release
dotnet test Dockaroo.sln -c Release
```

Expected: Core 298 passed + 1 skipped (the real-data import test, opt-in via
`DOCKAROO_LEGACY_ROOT`), App 167 passed. Known flakes, rerun before chasing them:
`SingleInstanceServerTests.OccupiedPipeName` while the user's resident runs, and
`TaskbarLocatorTests.ElementRectAt_TheComBindingAnswers` (reads whatever is at the screen centre).

Install on this machine: `E:\SOFTWARE\Dockaroo` (framework-dependent publish), data in
`%LOCALAPPDATA%\Dockaroo`. Update = the user quits from the tray, then
`dotnet publish src\Dockaroo.App -c Release -o "E:\SOFTWARE\Dockaroo" --self-contained false`.
Never kill the user's running app to free a locked folder; ask, or build to a scratch output.
SANDBOX TRAP (2026-10-16): when the agent runs inside the Claude desktop app, its shell is in
that app's MSIX sandbox. New files and folders it creates under `%LOCALAPPDATA%` are silently
redirected to `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\...`, visible only
to processes started from the agent. So: never create or seed the data root from the agent, and
never start the resident from the agent to "test" the user's setup (it sees the sandbox copy, and
its drag chip hands Explorer paths it cannot see). Changes the user must see under AppData are
done by the user (give them the command), or by Dockaroo started by the user. Writes to E:\ and to existing files outside AppData\Local (e.g. the pinned .lnk files) are not redirected.
Start the resident with a plain `Start-Process <exe>`, never `-WindowStyle Hidden`: the hidden
show state carries over to the app's first real window, so the editor opens invisible.

## Working rules (user)

- Commit only when the user asks; never push. There is no remote yet.
- TESTING.md is binding: no mouse/keyboard injection, no focus stealing, no computer-use tools, no
  desktop screenshots; test windows go off-screen or behind all the user's windows, muted;
  terminals and spawned processes run hidden.
- Each task: a short plan, the change, tests that assert content (not just "not null"), a visual
  gate for UI changes (capture tools), then a reviewer subagent pass on the diff before asking to
  commit. Blocking review findings get fixed; deferred ones go to NEXT.md "Open items" with an ID.
- One cleanup task every 2-3 feature tasks to drain deferred notes.
- Record user rulings (placement, look, behaviour) in NEXT.md with the date and keep them.

## Pins and AppUserModelIDs

- Each group's .lnk targets `Dockaroo.exe "<group>"` and carries AppUserModelID
  `Dockaroo.menu.<group>` with spaces as underscores (`Dockaroo.menu.PC_STUFF`). The open popup
  sets the same id on its window so it merges under the pinned button (selected state).
- Silent programmatic pinning is impossible on Windows 11 25H2; the editor hands the user a drag
  chip. Pinned buttons are copies in
  `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar`; to retarget a pin,
  write a fresh link with `Dockaroo.exe --create-link "<group>"` and copy it over the pinned file
  (same file name), backing the old one up first.
- Renaming the product, moving the exe or changing the AUMID scheme breaks every pin.

## Code conventions and gotchas

- C#: nullable on, file-scoped namespaces, XML doc comments on types and non-trivial members.
  Grow large classes through `partial` files named by responsibility (EditorForm.Layout.cs, ...).
- Line endings: CRLF in the working tree (`core.autocrlf=true`, `.gitattributes text=auto`). When
  editing through scripts, preserve the file's existing line endings.
- The Write tool turns `\uXXXX` escapes into raw characters. Glyph constants
  (`Editor/Theming/EditorTheme.cs`, `Glyphs`) must stay as escapes: edit them with a script
  that builds the backslash with `chr(92)`, or with the Edit tool on a small span.
- WinForms test quirks: `Control.Visible` reads false on never-shown forms (assert on state
  instead); `PerformClick` does nothing on unshown forms (invoke `OnClick` via reflection);
  `Timer` is ambiguous - write `System.Windows.Forms.Timer`; xUnit theories cannot take internal
  enums as parameters (pass strings).
- WFO1000 is suppressed in the App project (no designer files; everything is built in code).
- DPI: PerMonitorV2. The popup measures in physical pixels via `PopupMetrics.ForDpi`; forms use
  `AutoScaleMode.Dpi` with `LogicalToDeviceUnits`.
- Theme: `ThemeService` resolves System mode from HKCU `AppsUseLightTheme` and marshals changes to
  the UI thread; owner-drawn controls implement `IThemedControl`; DWM title bar attributes 20/35/36
  and `SetWindowTheme("DarkMode_Explorer")` for scrollbars.
- `.NET 10` SDK is not installed (only 5.0.415 and 9.0.317). Ask before installing anything.
