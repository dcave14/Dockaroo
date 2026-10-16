using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.App.Editor.Controls;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;

namespace Dockaroo.App.Editor;

/// <summary>
/// Group icon authoring ("Change icon"): an image, an .ico, or the icon of a program or shortcut
/// becomes the group's GroupIcon.ico, which its taskbar shortcut and the sidebar use. The choice
/// is part of the edit buffer: it shows immediately in the identity card and is written on Save.
/// </summary>
internal sealed partial class EditorForm
{
    private const string IconFileFilter =
        "Icons and images (*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif"
        + "|Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk"
        + "|All supported files|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.exe;*.lnk";

    private readonly ThemedButton _changeIcon = new("Change icon", ButtonKind.Secondary, "\uE8B9");
    private readonly ContextMenuStrip _iconMenu = new();
    private readonly ToolStripMenuItem _chooseIcon = new("Choose image or icon...");
    private readonly ToolStripMenuItem _defaultIcon = new("Use the default icon");

    // Pending icon change: new .ico bytes, or removal of the custom icon. Null/false = unchanged.
    private byte[]? _pendingIcon;
    private bool _pendingIconRemoval;

    private void BuildIconPicker()
    {
        _changeIcon.AccessibleName = "Change group icon";
        _chooseIcon.Click += (_, _) => ChooseGroupIcon();
        _defaultIcon.Click += (_, _) => UseDefaultIcon();
        _iconMenu.Items.Add(_chooseIcon);
        _iconMenu.Items.Add(_defaultIcon);
        _changeIcon.Click += (_, _) =>
        {
            _defaultIcon.Enabled = HasCustomIcon;
            _iconMenu.Renderer = new ThemedMenuRenderer(_theme.Tokens);
            _iconMenu.Show(_changeIcon, new Point(0, _changeIcon.Height));
        };
        _identityCard.Controls.Add(_changeIcon);
    }

    /// <summary>True when the group will have its own icon after the next Save.</summary>
    private bool HasCustomIcon =>
        _pendingIcon is not null
        || (!_pendingIconRemoval && _savedName is not null && GroupIconPathOrNull(_savedName) is not null);

    private void ChooseGroupIcon()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose a group icon",
            Filter = IconFileFilter,
            FilterIndex = 3,
            CheckFileExists = true,
            RestoreDirectory = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (SetPendingIconFromFile(dialog.FileName) is { } error)
        {
            UpdateFooter(StatusKind.Error, error);
        }
    }

    /// <summary>
    /// Converts <paramref name="path"/> into the pending group icon. Returns the error text when
    /// the file is not a usable image, icon, program or shortcut; the buffer is unchanged then.
    /// </summary>
    internal string? SetPendingIconFromFile(string path)
    {
        byte[] ico;
        try
        {
            ico = BuildGroupIcon(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or OutOfMemoryException or InvalidDataException)
        {
            // GDI+ reports an unreadable image as OutOfMemoryException.
            return $"'{Path.GetFileName(path)}' cannot be used as an icon: {ex.Message}";
        }

        // Picking the icon the group already has is not an edit.
        if (_savedName is not null && GroupIconPathOrNull(_savedName) is { } current && !_pendingIconRemoval)
        {
            try
            {
                if (File.ReadAllBytes(current).AsSpan().SequenceEqual(ico))
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable current icon: treat the pick as a change.
            }
        }

        _pendingIcon = ico;
        _pendingIconRemoval = false;
        ShowIconInWell(ico);
        MarkDirty();
        return null;
    }

    private void UseDefaultIcon()
    {
        if (!HasCustomIcon)
        {
            return; // already the default: nothing to change
        }

        _pendingIcon = null;
        _pendingIconRemoval = true;
        ShowIconInWell(null);
        MarkDirty();
    }

    /// <summary>The .ico bytes for a picked file: .ico as is, images re-encoded, programs extracted.</summary>
    internal static byte[] BuildGroupIcon(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".ico":
            {
                var bytes = File.ReadAllBytes(path);
                using var check = new Icon(new MemoryStream(bytes)); // throws on a broken file
                return bytes;
            }

            case ".exe" or ".lnk":
            {
                var result = IconExtractor.Extract(IconSource.FromPath(path), 256);
                using var bitmap = result.Bitmap ?? throw new InvalidDataException(result.Error ?? "No icon was found.");
                return IcoWriter.Write(bitmap);
            }

            default:
            {
                using var image = Image.FromFile(path);
                return IcoWriter.Write(image);
            }
        }
    }

    /// <summary>
    /// Writes (or removes) GroupIcon.ico after a successful group save. Returns a note for the
    /// status line, or sets <paramref name="error"/> and KEEPS the pending change (the buffer
    /// stays dirty) when the file could not be written.
    /// </summary>
    private string? ApplyPendingIcon(string groupName, out string? error)
    {
        error = null;
        if (_pendingIcon is null && !_pendingIconRemoval)
        {
            return null;
        }

        var iconFile = Path.Combine(_store.GetGroupDirectory(groupName), GroupIconFileName);
        var tempFile = iconFile + ".tmp";
        try
        {
            if (_pendingIcon is not null)
            {
                // Temp file + move: a crash or a full disk never leaves a truncated icon behind.
                File.WriteAllBytes(tempFile, _pendingIcon);
                File.Move(tempFile, iconFile, overwrite: true);
            }
            else if (File.Exists(iconFile))
            {
                File.Delete(iconFile);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A leftover temp file is harmless; the next save overwrites it.
            }

            error = $"The group was saved, but its icon could not be written ({ex.Message}).";
            return null;
        }

        _pendingIcon = null;
        _pendingIconRemoval = false;

        // An existing taskbar shortcut picks up the new icon; a pinned copy keeps its own.
        var lnk = Path.Combine(ShortcutsDirectory, groupName + ".lnk");
        if (!File.Exists(lnk))
        {
            return null;
        }

        var relinked = GroupLnkWriter.Create(groupName, Application.ExecutablePath, ShortcutsDirectory, iconPath: GroupIconPathOrNull(groupName), groupStore: _store);
        return relinked.Success
            ? "Taskbar shortcut updated; a pinned button shows the new icon after re-pinning."
            : $"The taskbar shortcut could not be updated ({relinked.Error}).";
    }

    /// <summary>Shows pending .ico bytes (or the app logo when null) in the identity card.</summary>
    private void ShowIconInWell(byte[]? ico)
    {
        var previous = _iconWell.GroupIcon;
        _iconWell.GroupIcon = null;
        previous?.Dispose();
        if (ico is not null)
        {
            try
            {
                _iconWell.GroupIcon = new Icon(new MemoryStream(ico), new Size(LogicalToDeviceUnits(40), LogicalToDeviceUnits(40)));
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException)
            {
                // Unreadable bytes cannot happen after BuildGroupIcon; keep the logo if they do.
            }
        }

        _iconWell.Invalidate();
    }
}
