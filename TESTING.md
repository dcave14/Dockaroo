# TESTING

## Standing rule: never drive the user's desktop (user, 2026-10-15)

- Terminal rule (user, 2026-10-15): "if using terminal, do it behind all windows, not on top of
  them" - a terminal popping up on top can disturb whatever the user is doing. Keep terminals
  behind all windows (minimized/hidden); never pop a console on top. Work is NOT stopped - only
  the popups are.
- Shell commands: run them through ONE persistent workspace terminal session; when a command must
  spawn anything, wrap it hidden (`Start-Process -WindowStyle Hidden -Wait` with output redirected
  to temp files). Never `start`, never a fresh console window on top.
- Test/UI windows (existing policy): ShowWithoutActivation, behind the user's apps;
  never Activate/SetForegroundWindow. Screenshots = PrintWindow only.

Runtime verification must never touch the user's real input or cover their windows. The user works on
this machine while agents test.

FORBIDDEN in every test run:
- Mouse/keyboard injection: SendInput, mouse_event, keybd_event, SetCursorPos, SendKeys, UI Automation
  clicks, real drag-drop, tray-icon clicks.
- Focus stealing: SetForegroundWindow, Activate(), TopMost windows, anything shown on the visible
  desktop (including the taskbar area).
- computer-use / desktop-control MCP tools.
- Full-desktop screenshots (they capture the user's browser, mail, etc.).
- Writing to the user's live legacy-app folder (the one `DOCKAROO_LEGACY_ROOT` names when set) in
  any way; the real-data import test only reads it.
- BEHIND THE USER'S APPS, MUTED (user rule, 2026-10-15): the tester may need windows and dialogs -
  that is fine - but they must sit BEHIND all of the user's windows. Never activate, never TopMost;
  push them to the bottom of the Z-order (SetWindowPos HWND_BOTTOM + SWP_NOACTIVATE) or place them
  off-screen. Mute them: no SystemSounds, no audible alerts. If anything lands on top of the user's
  apps, that is a violation - report it.

## Hidden verification recipe (WinForms)

1. Harness: a small C# or PowerShell + Add-Type program under .opencode/tasks/temp-scripts/ that
   references the Release output (src/Dockaroo.App/bin/Release/net9.0-windows/*.dll).
2. Show every form off-screen: StartPosition = Manual, Location = (-32000, -32000),
   ShowInTaskbar = false. Never call Activate().
3. Drive the UI programmatically: invoke button handlers / public methods directly, reflection for
   private members. Drag-drop = call the Drop handler's code path with synthetic paths.
   Tray menu = inspect the built menu items' Text via reflection; never click the tray icon.
4. Capture visuals with PrintWindow (user32) of the off-screen window into a Bitmap -> PNG. Never
   use desktop/screen capture. PrintWindow flags: a never-composed (never shown) window renders as
   an all-black bitmap under PW_RENDERFULLCONTENT (= 2) - use plain PrintWindow (WM_PRINTCLIENT)
   there; for a window that HAS been shown, even off-screen, PW_RENDERFULLCONTENT is fine and gives
   the DWM-composed frame. Prefer WM_PRINTCLIENT as the safe default.
   Forms that derive their icon from Application.ExecutablePath must be tested against the real exe
   (e.g. host it via DOTNET_STARTUP_HOOKS); a renamed or copied harness exe reports the wrong icon.
   Product behaviour, not a rule violation: the app's own popup takes foreground and focus when it
   is shown (button click, launch args, queued startup request). Keep it off-screen as usual.
   Caveats: Control.Visible reads false for never-Shown forms - assert on handler state / text / files
   instead; MessageBoxes are OK if the tester needs them (keep them behind all apps / off-screen,
   muted) and are dismissed with WM_COMMAND IDOK (PostMessage).
5. Save screenshots to .opencode/tasks/temp-screenshots/<task>-<n>.png and list every path in the
   report. Delete a task's screenshots when the task closes.
6. Kill every process the harness started.

## Measuring the user's screenshot evidence (read-only)

- The snips in `%USERPROFILE%\Pictures\Screenshots\` are read-only evidence: never move, rename,
  edit or delete them. Measure them in place with short Python/PIL scripts kept under
  `.opencode/tasks/temp-scripts\`, and save the raw outputs as `measure-<task>-*.txt` there.
- Report a compact table: element bbox (x,y,w,h), colours as `rgb()`, pitch/gaps/edges in image px.
  Note the DPI scale when inferable (taskbar strip = 48 logical px at 100%; 24px icons, 40px buttons,
  44px pitch are the 100% metrics).
- Backdrop-dependent values (acrylic fill, light-catch edges): sample 2+ sources/screenshots, record
  both readings plus the CONCERN, and pick the closeup as the canonical value for replication.
- Taskbar-indicator captures: the user's taskbar may be auto-hidden - PrintWindow(Shell_TrayWnd) then
  returns black. Fallback: WGC window-capture OF THE SHELL_TRAYWND WINDOW ONLY (never full-screen),
  and only while the bar is revealed. Allowed for taskbar-indicator checks only; the
  no-desktop-screenshot rule is unchanged.
- A test window placed off-screen registers NO taskbar presence at all. Use on-screen-but-behind
  (ShowWithoutActivation + HWND_BOTTOM) for anything that must appear on the taskbar.
- Truncated-text detection: a PrintWindow capture alone cannot prove a string ellipsised. Render the
  candidate strings with TextRenderer into off-screen bitmaps (full string vs expected ellipsised
  form, same font/size/box), then compare as BINARY INK MASKS aligned on their bounding boxes -
  raw RGB never matches (ClearType AA deltas <= 21 / 528 px). Only a 0-diff ink-mask match proves it.
- Release-freshness check before any visual gate: compare the built dll timestamp against the newest
  touched source file and REBUILD if stale (a gate on an old binary wastes the round).
- If the Release output is LOCKED (a resident instance holds the dll), do not kill the user's app -
  build to a scratch output dir (`dotnet build -o <temp>`) and capture from there; record which
  binary the capture used.

## Report contract

Every tester report: verdict first (PASS/FAIL/BLOCKED per checklist letter), one line of evidence each
(path:line, file path, or screenshot path), and a final "Doc gaps:" line naming anything the docs
should mention but do not.

Build/test commands: see BUILD.md. Logs go to the console only - if a run is interrupted, its evidence
is lost, so write intermediate results to .opencode/tasks/temp-scripts/*.txt as you go.
