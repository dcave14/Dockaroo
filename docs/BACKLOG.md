# Dockaroo: improvement backlog

Open items from the improvement review (2026-10-15). P0, P1, P2-4, P2-5, P2-6, P2-11 and P3-1's
first slice are done. Scheduling status lives in NEXT.md.

Ground rules for every item:
- The popup show path does zero IO.
- Follow TESTING.md: windows go off-screen or behind the user's apps, no input injection, no focus
  stealing, no desktop screenshots. PrintWindow captures for visuals.
- **Tests assert content, not just presence.** "Bitmap is not null and 48x48" is not a test.

## P2: correctness and robustness

- **P2-1 Image icons kept at full resolution.** `IconExtractor` returns images at their source size,
  so a 4000x3000 picture is stored, cached to disk and rescaled on every paint and hover. Resize to
  the requested size like every other kind (`ResizeTo`). Test: a 1000x1000 PNG comes back at the
  requested size.
- **P2-2 Hand edits and app updates aren't picked up.** Groups are cached forever (fine on the hot
  path), but edits to `group.xml` outside the editor need a restart, and icon keys have no file
  timestamp, so an updated app keeps its old icon. Add a `FileSystemWatcher` on `config/` that
  reloads changed groups on a background thread and swaps them in on the UI thread, plus a tray
  "Reload" item that also clears missing-icon results. Optionally put the target's last-write time
  in the cache key, computed during preload only.
- **P2-3 `IPropertyStore.Commit()` is never called.** `GroupLnkWriter.SetAppUserModelId` calls
  `SetValue` and then saves. Microsoft's documented sequence is SetValue, Commit, Save.
- **P2-7 Rename edge cases.** If `_store.Delete(previousName)` fails after the new save, both groups
  exist and no message is shown. A case-only rename ("my group" -> "My Group") keeps the old folder
  casing, so `ListNames` and the tray menu show the old casing; rename the directory through a temp
  name.
- **P2-8 Error matching by message text.** The editor decides where to show a save error with
  `Contains("already exists")`. Add a status enum to `GroupSaveResult` (`Saved`, `AlreadyExists`,
  `Invalid`, `IoError`), mirroring `GroupLoadResult`.
- **P2-9 Importer drops whole groups over one character.** Legacy names may contain `-`, `&`, `'`,
  `.` and similar, which `Group.IsValidName` rejects. Sanitise them (disallowed characters to a
  space, collapse runs, trim) and report the rename. `isWindowsApp=true` entries are ignored today:
  map them to a store-app target (the legacy `FilePath` holds the app id) or report them.
- **P2-10 Cold-load keeps a name in `_coldLoads` forever if the background task throws.**
  `TrayContext.BeginColdLoad`: wrap the task body in try/finally so `_coldLoads.Remove` always runs.

## P3: structure and tests

- **P3-1 App tests** - first slice done; extend to `EditorForm` save/rename flows through the
  off-screen harness and `TrayContext` request queueing.
- **P3-2 Remove duplicated COM interop.** `IShellLinkW`/`IPersistFile`/`IPropertyStore`/`PROPVARIANT`
  handling is copy-pasted in `LnkResolver.cs` and `GroupLnkWriter.cs` (and `PopupForm` has its own
  window-property block). Move it into one internal `ShellInterop` class. `TryCreateIconSource` is
  duplicated in `PopupForm.cs` and `TrayContext.cs`; put a single `IconSource.TryFromPath` in Core.
- **P3-3 Split large files.** The editor is split already; `GroupStore.cs` (4 types) remains.
- **P3-4 Trim comment volume.** Keep "legacy X did Y" notes only where behaviour is surprising.
- **P3-5 Clean scratch data.** Tests must use temp roots only; the real data root must never get
  scratch groups (`Ten_Group`, `Test_Group` exist in it today and can be deleted).

## Process

1. **Visual gate for every UI task**: PrintWindow captures (off-screen) of the popup and the editor
   on Dark and Light; the reviewer looks at them.
2. **Content assertions required**: a review fails any test whose only assertions are "not null",
   "count == N" or "file exists" for an output that has content.
3. **Open findings ledger**: review findings go into NEXT.md "Open items" with an ID and stay there
   until a commit closes them.
4. **One cleanup task every 2-3 feature tasks** to drain the deferred-notes list.
