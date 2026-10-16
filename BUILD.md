# Dockaroo - build and run

## Prerequisites

- Windows.
- .NET SDK 9 (for example 9.0.x).

## Build

    dotnet build Dockaroo.sln -c Release

## Test

    dotnet test Dockaroo.sln -c Release

## Run

Debug:

    dotnet run --project src/Dockaroo.App

Release:

    dotnet run --project src/Dockaroo.App -c Release

Or run the built exe directly:

    src/Dockaroo.App/bin/Release/net9.0-windows/Dockaroo.exe

The app starts resident in the notification area (a plain start also opens the editor) and
preloads every group plus the icons the
popup draws. The named pipe starts serving before that preload finishes, so a launch that
arrives mid-preload is queued and shown once preload completes, never dropped. Launching the
exe a second time captures the cursor and signals the resident instance over the named pipe to
show the popup near that point, then exits (exit code 0). A launch that starts the resident
itself (no other instance running) takes its own command line as the first popup request, so a
pinned group button works on the first click after a reboot.

The popup takes the foreground and focus like a normal popup window whenever it is shown, by button
click, launch args or a queued startup request. That is expected product behaviour; test harnesses
must keep it off-screen or behind the user's apps (TESTING.md rules).

The tray icon menu can show the popup for a preloaded group (directly when there is one
group, as a submenu of group names otherwise); its Exit item stops the resident instance.

## Popup payload (manual testing)

Every launch after the first is a transient second instance that sends one line to the
resident:

    popup:<x>:<y>:<group-name>

- `popup` - literal keyword.
- `<x>`/`<y>` - cursor position at launch time in virtual-screen coordinates. The resident
  places the popup near this point, next to the taskbar edge of the screen that contains it
  (bottom/top/left/right, hidden taskbar included).
- `<group-name>` - everything after the third colon, so spaces are fine. The second instance
  builds it from its command line (`Dockaroo.exe "My Group"`; unquoted words are
  joined with spaces).

A plain start of `Dockaroo.exe` (no group name, e.g. double-clicking it) opens the editor: the
second instance sends the payload `editor`, and a resident started that way opens the editor
once its groups are loaded. The bare `popup` payload is still accepted and shows the first
configured group.

Groups are read from `<data-root>\config/<Group-Name>/group.xml` (spaces in the name become
underscores in the folder name), and the shared PNG icon cache lives in
`<data-root>\icon-cache/`. The data root is `%LOCALAPPDATA%\Dockaroo\` by default;
a `portable.txt` file next to the executable switches it to the executable's own folder
(legacy layout; the first start after upgrading copies old exe-adjacent `config/` groups and
`Shortcuts/` files over, never overwriting or deleting). Preloaded groups are served from
memory with no disk IO on the show path. A group that was not preloaded (created while the
resident was running) is loaded and icon-preloaded on a background thread, and its popup opens
when that finishes; the UI thread never runs that IO.

Minimal config for a manual test (`Width`, `AllowOpenAll` and `Opacity` shown with their
defaults; every field may be omitted to keep its default):

    <Group>
      <Name>My Group</Name>
      <Width>6</Width>
      <AllowOpenAll>false</AllowOpenAll>
      <Opacity>10</Opacity>
      <Shortcuts>
        <Shortcut>
          <TargetPath>C:\Windows\System32\notepad.exe</TargetPath>
          <Name>Notepad</Name>
        </Shortcut>
      </Shortcuts>
    </Group>

- `Width` (1..20, default 6): shortcuts per row.
- `AllowOpenAll` (default false): true enables Ctrl+Enter to launch every entry.
- `Opacity` (default 10): 0 = solid, 100 = fully transparent.
- `ColorHex` (default `#1F1F1F`): popup background.

## Pin a group to the taskbar (--create-link)

Each group can get its own taskbar button through a shortcut that carries the group's
AppUserModelID. Create it with:

    src/Dockaroo.App/bin/Release/net9.0-windows/Dockaroo.exe --create-link "My Group"

The command writes `My Group.lnk`, prints its full path and exits 0. It runs before the
single-instance check, so it works while the resident is up and never hands off. Without an
output directory the shortcut lands in `<data-root>\Shortcuts` (the editor's "Create shortcut"
default is the same folder); a second argument picks another location:

    ... --create-link "My Group" C:\Users\me\Desktop\Pins

The shortcut targets the app executable with the quoted group name as its argument
(`Dockaroo.exe "My Group"`), exactly the second-instance form above, so double-clicking
it opens that group's popup through the resident handoff. It also records the group's
AppUserModelID (`Dockaroo.menu.<group>`, spaces as underscores: `Dockaroo.menu.PC_STUFF`) and
uses the group's `GroupIcon.ico` when it has one, the exe icon otherwise. Re-running the command replaces the `.lnk` in
place, like the legacy ShellLink save. The group must exist in the config store
(`<data-root>\config/<Group-Name>/group.xml`, as above): a valid-but-unknown name
prints a "group not found" error to stderr and exits 1 without writing a `.lnk`, so the
command never leaves a dead shortcut that opens nothing. It exits 0 when the shortcut is
written (the path is printed to stdout), exits 1 for an invalid or unknown group, or an IO
problem, and exits 2 when arguments are missing (the usage line is printed to stderr). The
legacy client staged these files in a `Shortcuts` folder next to the exe; the data root's
`Shortcuts` folder replaces that.

To pin it, drag the `.lnk` file onto the taskbar. Windows copies the shortcut into its pinned
items at that moment, so the source file is only needed for the drag; re-create and re-pin
after the app path or the link contents change.

Running state: the pinned button starts a transient second instance that sets the group's
AppUserModelID, hands the request to the resident and exits (about 65 ms), so the button shows
its running state only as a brief flash during the handoff - the resident owns the tray, not
that button. With no resident running, a click starts the resident and opens the group's
popup once preload finishes: the resident treats its own command line as the first request.

Rename warning: the AppUserModelID, the shortcut target and its arguments are baked into the
`.lnk` at creation time. Renaming the product or moving the executable breaks every pinned
link; delete the stale `.lnk`, run `--create-link` again and pin the new one.

## Popup size and placement

- Size math: one cell is a 40x40 plate (icon 24x24) at a 44 px pitch in both axes. For E
  entries and Width W, perRow = min(E, W) and rows = ceil(E/perRow); the popup is
  (8 + 44*perRow) x (7 + 44*rows) px. The width fits the real cells, so the grid never
  reserves empty columns: 3 entries at Width 6 -> 140x51, 12 entries at Width 6 -> 272x95.
  Width is clamped to 1..20 by the size math.
- Taskbar clicks (user rulings 2026-10-15): the popup is centred on the clicked button and its
  bar-side edge tucks 2 px (scaled) under the taskbar, which covers it - one trim line, not two.
  The bar is the clicked taskbar window's real rectangle at click time (revealed, so this works
  with auto-hide); the button comes from a UI Automation hit test on the pipe thread (150 ms
  cap, cursor fallback). The tucked strip is cut out of the window (Region), and the popup stays
  where it opened when an auto-hide bar slides away; while the bar is away the clip is dropped
  so the popup shows its own bar-side trim (user ruling 2026-10-16), and it is re-applied when
  the bar comes back (the bar's rectangle is polled every 100 ms while the popup is open).
- Other opens (keyboard, no taskbar under the cursor): above the click point, clamped to the
  screen's working area; the legacy hidden-bar band (35 px / 45 px, from the
  legacy app's `frmMain.cs`) only applies there.
- Manual launch tests: use a target that stays alive long enough to observe, for example
  `notepad.exe` or `ping -n 30 127.0.0.1`. `cmd /c timeout 1` exits instantly in this
  environment and is a bad smoke target.

## Install and update (this machine)

The app is installed (framework-dependent publish) in `E:\SOFTWARE\Dockaroo`; the user's
taskbar pins and the shortcuts in `%LOCALAPPDATA%\Dockaroo\Shortcuts` point at
`E:\SOFTWARE\Dockaroo\Dockaroo.exe`. Groups, icons and appearance live in
`%LOCALAPPDATA%\Dockaroo`, independent of the install folder.

To update, quit the app from the tray (right-click > Exit), then:

```
dotnet publish src\Dockaroo.App -c Release -o "E:\SOFTWARE\Dockaroo" --self-contained false
```

The pins keep working: same exe path, same AppUserModelIDs. Do not delete or move the install
folder - the pins would open nothing until they are re-created (`--create-link`, then copy over
the pinned .lnk files, or re-pin with the editor's drag chip).

## Target framework

All three projects target `net9.0-windows` and keep the TFM visible in each `.csproj`, so
bumping to `net10.0-windows` is one line per file. It is net9.0-windows today only
because the .NET 10 SDK is absent on this machine (only 5.0.415 and 9.0.317 are installed).
