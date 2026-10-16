using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Linking;

namespace Dockaroo.App.Editor;

/// <summary>Save (validation, rename follow-up) and the "Create taskbar shortcut" handoff.</summary>
internal sealed partial class EditorForm
{
    internal const string SavedStatus = "All changes saved";

    /// <summary>What the button does after the shortcut has been written.</summary>
    internal enum PinHandoff
    {
        /// <summary>No matching pin exists: offer the drag chip (the only pin gesture).</summary>
        ShowDragChip,

        /// <summary>A matching pin already exists: report it; a chip would be pointless.</summary>
        AlreadyPinned,
    }

    /// <summary>
    /// Validates and saves the edit buffer. On failure the edits stay in place, the error is
    /// shown inline (name / shortcuts) or in the footer, and the buffer stays dirty.
    /// </summary>
    private void SaveGroup()
    {
        ClearNameError();
        _shortcutError.Visible = false;

        var name = _name.Text;
        if (name.Length == 0)
        {
            ShowNameError("Must select a name");
            return;
        }

        if (!Group.IsValidName(name))
        {
            ShowNameError(
                $"Name must not have special characters, must be at most {Group.MaxNameLength} characters and must not have leading or trailing spaces.");
            return;
        }

        if (_entries.Entries.Count == 0)
        {
            ShowShortcutError("Must select at least one shortcut");
            return;
        }

        foreach (var entry in _entries.Entries)
        {
            if (!entry.TryValidate(out var entryError))
            {
                ShowShortcutError(entryError!);
                return;
            }
        }

        _group.Name = name;
        _group.Width = _width.Value;
        if (_transparencyTouched)
        {
            _group.Opacity = _transparency.Value;
        }
        _group.AllowOpenAll = _allowOpenAll.Checked;
        _group.ColorHex = CurrentColorHex();
        _group.Shortcuts = _entries.Entries.ToList();

        var isRename = _savedName is not null && !string.Equals(_savedName, name, StringComparison.OrdinalIgnoreCase);
        var result = _store.Save(_group, overwrite: _savedName is not null && !isRename);
        if (!result.Success)
        {
            if (result.Error is not null && result.Error.Contains("already exists", StringComparison.Ordinal))
            {
                ShowNameError(result.Error);
            }
            else
            {
                UpdateFooter(StatusKind.Error, result.Error ?? "The group could not be saved.");
            }

            return;
        }

        var previousName = _savedName;
        _savedName = name;
        string? relinkStatus = null;
        string? carryWarning = null;
        var wasPinned = false;
        if (isRename && previousName is not null)
        {
            // Legacy rename behavior: the old config is removed once the new one is written.
            // The group's own icon/image files move along first, or the delete destroys them.
            carryWarning = CarryGroupFiles(previousName, name);
            _store.Delete(previousName);
            relinkStatus = RelinkAfterRename(previousName, name, out wasPinned);
        }

        var iconStatus = ApplyPendingIcon(name, out var iconError);
        _dirty = iconError is not null; // the unwritten icon is still an unsaved edit
        if (_pendingIcon is null && !_pendingIconRemoval)
        {
            LoadGroupIconIntoWell();
        }

        Text = "Edit group";
        RefreshSidebar(_savedName);
        UpdateHeading();
        if (iconError is not null)
        {
            UpdateFooter(StatusKind.Error, string.Join(" ", new[] { iconError, carryWarning, relinkStatus }.Where(part => part is not null)));
        }
        else
        {
            var status = string.Join(" ", new[] { SavedStatus + ".", carryWarning, relinkStatus, iconStatus }.Where(part => part is not null));
            UpdateFooter(StatusKind.Saved, status);
        }

        GroupsChanged?.Invoke(this, EventArgs.Empty);
        if (wasPinned)
        {
            // A pinned button is the one place the break is visible, so say it out loud.
            MessageBox.Show(this, $"'{previousName}' was renamed to '{name}'. {RepinMessage}", AppConstants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>
    /// Writes the group's taskbar .lnk and hands it over as a drag chip. A dirty or never-saved
    /// group is saved first; if that fails nothing else happens and the error stays visible.
    /// </summary>
    private void CreateTaskbarShortcut()
    {
        if (_dirty || _savedName is null)
        {
            SaveGroup();
            if (_dirty || _savedName is null)
            {
                return;
            }
        }

        var iconPath = GroupIconPathOrNull(_savedName);
        var result = GroupLnkWriter.Create(
            _savedName,
            Application.ExecutablePath,
            ShortcutsDirectory,
            iconPath: iconPath,
            workingDirectory: null,
            groupStore: _store);

        if (!result.Success)
        {
            UpdateFooter(StatusKind.Error, result.Error ?? "The shortcut could not be created.");
            MessageBox.Show(this, result.Error ?? "The shortcut could not be created.", AppConstants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UpdateFooter(StatusKind.Info, $"Created {result.LnkPath}");

        if (DecidePinHandoff(_savedName) == PinHandoff.AlreadyPinned)
        {
            UpdateFooter(StatusKind.Info, "Already pinned to the taskbar.");
            return;
        }

        ShowDragChip(_savedName, result.LnkPath!, iconPath);
    }

    /// <summary>
    /// Decides the post-create handoff from the current pin state: the clicked group only,
    /// checked (and repaired in place) right here - bounded IO on a user action, never on the
    /// popup show path. Factored out so tests can drive it with a fake cache.
    /// </summary>
    internal PinHandoff DecidePinHandoff(string groupName)
    {
        _pins.Refresh(new[] { new Group { Name = groupName } });
        return _pins.HasPin(groupName) ? PinHandoff.AlreadyPinned : PinHandoff.ShowDragChip;
    }

    /// <summary>
    /// Opens the drag-source chip for the human pin gesture (F-021: Windows 11 25H2 has no
    /// silent pin route). The chip carries the same icon the .lnk was written with, so the
    /// drag matches what lands on the taskbar.
    /// </summary>
    private void ShowDragChip(string groupName, string lnkPath, string? iconPath)
    {
        var chip = new DragPinChipForm(groupName, lnkPath, LoadShortcutIcon(iconPath));
        chip.Show(this);
    }

    private static Icon? LoadShortcutIcon(string? iconPath)
    {
        try
        {
            return iconPath is not null
                ? new Icon(iconPath)
                : Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
        {
            // A bad icon must not block the handoff; the chip then shows text only.
            return null;
        }
    }
}
