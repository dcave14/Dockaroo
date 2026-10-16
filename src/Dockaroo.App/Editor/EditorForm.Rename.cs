using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Linking;

namespace Dockaroo.App.Editor;

/// <summary>
/// Rename follow-up: a taskbar .lnk passes the group name (and AppUserModelID) baked in, so after
/// a rename the old shortcut would open nothing. The editor writes the shortcut under the new
/// name, removes the old one and tells the user to re-pin.
/// </summary>
internal sealed partial class EditorForm
{
    /// <summary>Shown after a rename of a group that had a taskbar shortcut or pin.</summary>
    internal const string RepinMessage = "Re-pin the taskbar button: the old one no longer works.";

    private const string LnkExtension = ".lnk";

    // Files a group folder carries besides its config (legacy layout, see LegacyConfigImporter).
    private static readonly string[] GroupFileNames = { GroupIconFileName, "GroupImage.png" };

    /// <summary>
    /// Copies the group's icon and image files from the old folder into the renamed one before
    /// the old folder is deleted. Returns a status warning when a copy failed, else null.
    /// </summary>
    private string? CarryGroupFiles(string previousName, string newName)
    {
        var from = _store.GetGroupDirectory(previousName);
        var to = _store.GetGroupDirectory(newName);
        var failures = new List<string>();
        foreach (var fileName in GroupFileNames)
        {
            var source = Path.Combine(from, fileName);
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(to);
                File.Copy(source, Path.Combine(to, fileName), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{fileName} ({ex.Message})");
            }
        }

        return failures.Count == 0 ? null : $"Not carried over to the renamed group: {string.Join(", ", failures)}.";
    }

    /// <summary>
    /// Replaces <paramref name="previousName"/>'s .lnk with one for <paramref name="newName"/>.
    /// Returns the status text to append, or null when the old group had neither a shortcut nor
    /// a known pin (nothing to repair). Runs on the Save click, never on the popup show path.
    /// </summary>
    private string? RelinkAfterRename(string previousName, string newName, out bool wasPinned)
    {
        // A pin is the user's own copy of the old .lnk and cannot be retargeted, so it can only
        // be reported. Checked for this one group here, on the Save click (bounded IO, same as
        // the Create-shortcut handoff), and before the old .lnk is removed.
        _pins.Refresh(new[] { new Group { Name = previousName } });
        wasPinned = _pins.HasPin(previousName);

        var directory = ShortcutsDirectory;
        var oldLnk = Path.Combine(directory, previousName + LnkExtension);
        if (!File.Exists(oldLnk))
        {
            return wasPinned ? RepinMessage : null;
        }

        var created = GroupLnkWriter.Create(
            newName,
            Application.ExecutablePath,
            directory,
            iconPath: GroupIconPathOrNull(newName),
            workingDirectory: null,
            groupStore: _store);
        if (!created.Success)
        {
            return $"The taskbar shortcut could not be renamed ({created.Error}). {RepinMessage}";
        }

        try
        {
            File.Delete(oldLnk);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Created {created.LnkPath}, but the old shortcut could not be removed ({ex.Message}). {RepinMessage}";
        }

        return $"Created {created.LnkPath}. {RepinMessage}";
    }

    /// <summary>Legacy icon convention: the group's own icon file lives in the group directory.</summary>
    private string? GroupIconPathOrNull(string groupName)
    {
        var iconFile = Path.Combine(_store.GetGroupDirectory(groupName), GroupIconFileName);
        return File.Exists(iconFile) ? iconFile : null;
    }
}
