using System.Drawing;
using System.Windows.Forms;
using Dockaroo.Core;

namespace Dockaroo.App.Editor;

/// <summary>Adding shortcuts (dialog and drop), the selected-shortcut card, colours and inline errors.</summary>
internal sealed partial class EditorForm
{
    private const string NoSelectionHint = "Select a shortcut to edit its arguments and working directory.";

    private void AddShortcutsViaDialog()
    {
        using var dialog = new OpenFileDialog
        {
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Title = "Add shortcuts", CheckFileExists = true, CheckPathExists = true,
            Multiselect = true, DefaultExt = "exe", RestoreDirectory = true,
            Filter = "Exe or Shortcut (.exe, .lnk, .url)|*.exe;*.lnk;*.url",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddTargets(dialog.FileNames);
        }
    }

    private void AddTargets(IReadOnlyList<string> targetPaths)
    {
        var added = _entries.AddTargets(targetPaths);
        if (added < targetPaths.Count)
        {
            ShowShortcutError($"Max {EntryListControl.MaxEntries} shortcuts in one group");
        }
        else
        {
            _shortcutError.Visible = false;
        }

        if (added > 0)
        {
            _entries.SelectIndex(_entries.Entries.Count - 1);
        }
    }

    private void BrowseWorkingDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the working directory", UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_workingDirectory.Text) ? _workingDirectory.Text : string.Empty,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK && _entries.SelectedEntry is not null)
        {
            // The TextChanged handler writes the entry and marks the buffer dirty.
            _workingDirectory.Text = dialog.SelectedPath;
        }
    }

    /// <summary>Fills the selected-shortcut card; with no selection the card shows a hint and is disabled.</summary>
    private void UpdateSelectionPanel()
    {
        var entry = _entries.SelectedEntry;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            _arguments.Text = entry?.Arguments ?? string.Empty;
            _workingDirectory.Text = entry?.WorkingDirectory ?? string.Empty;
        }
        finally
        {
            _loading = wasLoading;
        }

        var enabled = entry is not null;
        _argumentsField.Enabled = enabled;
        _workingDirectoryField.Enabled = enabled;
        _browse.Enabled = enabled;
        ApplySelectionLabelColors();
        _selectedSummary.Text = entry is null ? NoSelectionHint : string.IsNullOrWhiteSpace(entry.Name) ? entry.TargetPath : entry.Name;
        _toolTip.SetToolTip(_selectedSummary, entry?.TargetPath ?? string.Empty);
    }

    // Not Label.Enabled: WinForms draws disabled labels etched, which clashes with the theme.
    private void ApplySelectionLabelColors()
    {
        var t = _theme.Tokens;
        var color = _entries.SelectedEntry is null ? t.Faint : t.Muted;
        _argumentsLabel.ForeColor = color;
        _workingDirectoryLabel.ForeColor = color;
    }

    private void PickCustomColor()
    {
        using var dialog = new ColorDialog { Color = _customColor, FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Color.ToArgb() != _customColor.ToArgb())
        {
            _customColor = dialog.Color;
            _background.Swatch = _customColor;
            _background.Invalidate();
            MarkDirty();
        }
    }

    private string CurrentColorHex()
    {
        return _background.SelectedIndex switch
        {
            1 => Group.LightColorHex,
            2 => Group.ToColorHex(_customColor),
            _ => Group.DarkColorHex,
        };
    }

    private void ClearNameError()
    {
        _nameError.Visible = false;
        _nameField.HasError = false;
    }

    private void ShowNameError(string message)
    {
        _nameError.Text = message;
        _nameError.Visible = true;
        _nameField.HasError = true;
        _toolTip.SetToolTip(_nameError, message);
        _name.Focus();
    }

    private void ShowShortcutError(string message)
    {
        _shortcutError.Text = message;
        _shortcutError.Visible = true;
        _toolTip.SetToolTip(_shortcutError, message);
    }

    private static List<string> GetDroppableFiles(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return new List<string>();
        }

        return files.Where(file => !string.IsNullOrEmpty(file) && IsDroppable(file)).ToList();
    }

    /// <summary>Legacy drop filter: .exe/.lnk/.url files and folders are accepted.</summary>
    private static bool IsDroppable(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }
}
