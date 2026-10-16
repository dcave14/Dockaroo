using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.App.Appearance;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Legacy;

namespace Dockaroo.App.Editor;

/// <summary>
/// Sidebar group list, switching groups behind the unsaved-changes guard, New group, legacy
/// import and Delete.
/// </summary>
internal sealed partial class EditorForm
{
    // Groups shown in the sidebar (from the last store read) and the images this form owns:
    // group .ico bitmaps by name, and logo fallbacks (redrawn per palette). Cache icons are not ours.
    private List<Group> _sidebarGroups = new();
    private readonly Dictionary<string, Image> _groupIconImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Image> _logoImages = new();

    /// <summary>Re-reads the groups from the store into the sidebar and highlights <paramref name="selectName"/>.</summary>
    private void RefreshSidebar(string? selectName)
    {
        _sidebarGroups = _store.LoadAll().ToList();
        DisposeImages(_groupIconImages.Values);
        _groupIconImages.Clear();
        var size = new Size(LogicalToDeviceUnits(20), LogicalToDeviceUnits(20));
        foreach (var group in _sidebarGroups)
        {
            if (GroupIconPathOrNull(group.Name) is not { } iconPath)
            {
                continue;
            }

            try
            {
                using var icon = new Icon(iconPath, size);
                _groupIconImages[group.Name] = icon.ToBitmap();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
            {
                // A bad icon file falls through to the next choice.
            }
        }

        BuildSidebarItems(selectName);
    }

    /// <summary>Rebuilds the sidebar rows from the last store read; no file IO.</summary>
    private void BuildSidebarItems(string? selectName)
    {
        DisposeImages(_logoImages);
        _logoImages.Clear();
        var items = new List<SidebarItem>();
        var selected = -1;
        foreach (var group in _sidebarGroups)
        {
            if (selectName is not null && string.Equals(group.Name, selectName, StringComparison.OrdinalIgnoreCase))
            {
                selected = items.Count;
            }

            items.Add(new SidebarItem(group.Name, SidebarImageFor(group)));
        }

        _sidebar.SetItems(items, selected);
    }

    /// <summary>
    /// The group's own icon file, else its first shortcut's icon if the resident already has it in
    /// memory (no extraction here), else the app logo.
    /// </summary>
    private Image? SidebarImageFor(Group group)
    {
        if (_groupIconImages.TryGetValue(group.Name, out var own))
        {
            return own;
        }

        if (_icons is not null && group.Shortcuts.FirstOrDefault() is { } first)
        {
            try
            {
                if (_icons.TryGet(IconSource.FromPath(first.TargetPath), PopupGeometryIconSize, out var cached) && cached?.Bitmap is { } bitmap)
                {
                    return bitmap;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or ObjectDisposedException)
            {
                // No cached icon for this target.
            }
        }

        // A copy: the sidebar images are disposed on rebuild, the cached logo is not ours.
        var logo = new Bitmap(PaletteLogos.Square(_theme.PaletteId, LogicalToDeviceUnits(20)));
        _logoImages.Add(logo);
        return logo;
    }

    private static int PopupGeometryIconSize => Dockaroo.Core.Popup.PopupGeometry.IconSize;

    // Logo fallbacks follow the palette; the group list itself is not re-read.
    private void RefreshSidebarImagesForTheme() => BuildSidebarItems(SidebarSelectionName);

    // On the Appearance page no group row is highlighted (the Appearance item is).
    private string? SidebarSelectionName => ShowingAppearance ? null : _savedName;

    private void DisposeSidebarImages()
    {
        DisposeImages(_logoImages);
        _logoImages.Clear();
        DisposeImages(_groupIconImages.Values);
        _groupIconImages.Clear();
    }

    private static void DisposeImages(IEnumerable<Image> images)
    {
        foreach (var image in images.ToList())
        {
            image.Dispose();
        }
    }

    private void OnSidebarGroupClicked(int index)
    {
        if (index < 0 || index >= _sidebar.Items.Count)
        {
            return;
        }

        var name = _sidebar.Items[index].Name;
        if (_savedName is not null && string.Equals(name, _savedName, StringComparison.OrdinalIgnoreCase))
        {
            // Back from the Appearance page to the group being edited: nothing to guard.
            ShowGroupPage();
            return;
        }

        if (!ConfirmLeaveDirty())
        {
            _sidebar.SelectedIndex = ShowingAppearance ? -1 : IndexOfSaved();
            return;
        }

        ShowGroupPage();

        if (!LoadGroupByName(name))
        {
            // "Don't save" must really discard, even when the other group cannot be opened.
            var error = _status.Text;
            RevertToSaved();
            UpdateFooter(StatusKind.Error, error);
        }
    }

    /// <summary>Loads a saved group into the editor; false (with the error in the footer) when it cannot be read.</summary>
    private bool LoadGroupByName(string name)
    {
        var result = _store.Load(name);
        if (result.Status != GroupLoadStatus.Loaded || result.Group is null)
        {
            UpdateFooter(StatusKind.Error, result.Error ?? $"'{name}' could not be loaded.");
            return false;
        }

        _group = result.Group;
        _savedName = _group.Name;
        LoadGroupIntoControls();
        _sidebar.SelectedIndex = IndexOfSaved();
        return true;
    }

    /// <summary>Back to the last saved state of the current group, or a fresh new group.</summary>
    private void RevertToSaved()
    {
        if (_savedName is not null && LoadGroupByName(_savedName))
        {
            return;
        }

        _group = new Group();
        _savedName = null;
        LoadGroupIntoControls();
        _sidebar.SelectedIndex = -1;
    }

    private void StartNewGroup()
    {
        if (!ConfirmLeaveDirty())
        {
            return;
        }

        _group = new Group();
        _savedName = null;
        ShowGroupPage();
        LoadGroupIntoControls();
        _sidebar.SelectedIndex = -1;
        _name.Focus();
    }

    /// <summary>
    /// Shows the app Appearance page. The group's edit buffer stays as it is (unsaved edits
    /// included) and comes back unchanged when the group is selected again.
    /// </summary>
    private void ShowAppearancePage()
    {
        _pinChip.Dismiss();
        if (_appearancePage is null || ShowingAppearance)
        {
            return;
        }

        _appearancePage.RefreshSelection();
        _showingAppearance = true;
        _appearancePage.Visible = true;
        _appearancePage.BringToFront();
        _content.Visible = false;
        _sidebar.SelectedIndex = -1;
        _sidebar.AppearanceSelected = true;
        SetGroupActionsVisible(false);
        AcceptButton = null; // Enter on the page must never run the hidden group Save
        Text = "Appearance";
        UpdateFooter(StatusKind.None, string.Empty);
    }

    private void ShowGroupPage()
    {
        if (!ShowingAppearance)
        {
            return;
        }

        _showingAppearance = false;
        _appearancePage!.Visible = false;
        _content.Visible = true;
        _sidebar.AppearanceSelected = false;
        _sidebar.SelectedIndex = IndexOfSaved();
        SetGroupActionsVisible(true);
        AcceptButton = _save;
        Text = _savedName is null ? "New group" : "Edit group";
        UpdateFooter(_dirty ? StatusKind.Dirty : StatusKind.None, _dirty ? "Unsaved changes" : string.Empty);
    }

    private void SetGroupActionsVisible(bool visible)
    {
        _groupActionsVisible = visible;
        _delete.Visible = visible;
        _createShortcut.Visible = visible;
        _showInExplorer.Visible = visible;
        _save.Visible = visible;
        _footer.PerformLayout();
    }

    private int IndexOfSaved()
    {
        if (_savedName is null)
        {
            return -1;
        }

        for (var i = 0; i < _sidebar.Items.Count; i++)
        {
            if (string.Equals(_sidebar.Items[i].Name, _savedName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Shows <see cref="_group"/> in the controls; the result is the clean (not dirty) state.</summary>
    private void LoadGroupIntoControls()
    {
        _pinChip.Dismiss();
        _loading = true;
        try
        {
            _name.Text = _group.Name;
            _width.Value = Math.Clamp(_group.Width, Group.MinWidth, Group.MaxWidth);
            _allowOpenAll.Checked = _group.AllowOpenAll;
            // The slider moves in steps of 10 within 10..90; the stored value (legacy or imported
            // groups can hold 35 or 100) is shown and saved unchanged until the slider is moved.
            _transparency.Value = Math.Clamp(_group.Opacity, MinTransparency, MaxTransparency);
            _transparencyValue.Text = $"{_group.Opacity}%";
            _transparencyTouched = false;

            var colorHex = Group.IsValidColorHex(_group.ColorHex) ? _group.ColorHex : Group.DarkColorHex;
            if (string.Equals(colorHex, Group.DarkColorHex, StringComparison.OrdinalIgnoreCase))
            {
                _background.SelectedIndex = 0;
            }
            else if (string.Equals(colorHex, Group.LightColorHex, StringComparison.OrdinalIgnoreCase))
            {
                _background.SelectedIndex = 1;
            }
            else
            {
                _customColor = ColorTranslator.FromHtml(colorHex);
                _background.SelectedIndex = 2;
            }

            _background.Swatch = _customColor;
            _entries.SetEntries(_group.Shortcuts);
            UpdateSelectionPanel();
        }
        finally
        {
            _loading = false;
        }

        _pendingIcon = null;
        _pendingIconRemoval = false;
        LoadGroupIconIntoWell();
        ClearNameError();
        _shortcutError.Visible = false;
        _dirty = false;
        Text = _savedName is null ? "New group" : "Edit group";
        UpdateHeading();
        UpdatePreview();
        UpdateFooter(StatusKind.None, string.Empty);
    }

    private void LoadGroupIconIntoWell()
    {
        var previous = _iconWell.GroupIcon;
        _iconWell.GroupIcon = null;
        previous?.Dispose();
        if (_savedName is not null && GroupIconPathOrNull(_savedName) is { } path)
        {
            try
            {
                _iconWell.GroupIcon = new Icon(path, new Size(LogicalToDeviceUnits(40), LogicalToDeviceUnits(40)));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
            {
                // Bad icon file: the app logo stays.
            }
        }

        _iconWell.Invalidate();
    }

    private void DeleteGroup()
    {
        if (_savedName is null)
        {
            return;
        }

        var confirmed = DeleteConfirmOverride?.Invoke(_savedName)
            ?? MessageBox.Show(
                this, $"Delete the group '{_savedName}'?", AppConstants.AppName,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        if (!confirmed)
        {
            return;
        }

        var deleted = _savedName;
        if (!_store.Delete(deleted))
        {
            UpdateFooter(StatusKind.Error, "The group could not be deleted.");
            return;
        }

        GroupsChanged?.Invoke(this, EventArgs.Empty);
        RefreshSidebar(selectName: null);
        _dirty = false;
        _savedName = null;
        if (_sidebar.Items.Count == 0 || !LoadGroupByName(_sidebar.Items[0].Name))
        {
            // Never leave the deleted group's name in the buffer: a later Save would recreate it.
            _group = new Group();
            _savedName = null;
            LoadGroupIntoControls();
            _sidebar.SelectedIndex = -1;
        }

        UpdateFooter(StatusKind.Info, $"Deleted '{deleted}'.");
    }

    /// <summary>
    /// Converts a legacy-app installation (the old app's folder, the one containing
    /// its "config" directory) into this store. Imported groups are reported and the sidebar
    /// and the resident refresh without a restart; a name that already exists is skipped and
    /// never overwritten.
    /// </summary>
    private void ImportLegacyGroups()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the folder of the old app installation (the one containing 'config')",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        LegacyImportResult result;
        try
        {
            result = LegacyConfigImporter.Import(dialog.SelectedPath, _store);
        }
        catch (Exception ex)
        {
            // The importer reports data problems as per-group results; this guards the UI
            // against anything unexpected so the editor never crashes on import.
            MessageBox.Show(this, ex.Message, AppConstants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (result.ImportedCount > 0)
        {
            RefreshSidebar(SidebarSelectionName);
            GroupsChanged?.Invoke(this, EventArgs.Empty);
        }

        MessageBox.Show(this, DescribeImportResult(result), AppConstants.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string DescribeImportResult(LegacyImportResult result)
    {
        if (result.Error is not null)
        {
            return result.Error;
        }

        if (result.Groups.Count == 0)
        {
            return "No groups were found in the selected folder.";
        }

        var lines = new List<string>
        {
            $"Imported {result.ImportedCount} of {result.Groups.Count} groups ({result.ImportedShortcutCount} shortcuts).",
        };

        foreach (var group in result.Groups)
        {
            if (group.Outcome == LegacyImportOutcome.Imported)
            {
                if (group.Message is not null)
                {
                    lines.Add($"'{group.GroupName}': {group.Message}");
                }
            }
            else
            {
                lines.Add($"{group.Outcome}: '{group.GroupName}': {group.Message}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
