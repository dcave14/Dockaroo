using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Icons;

namespace Dockaroo.App.Editor;

/// <summary>
/// The group editor, laid out per the design handoff (design/interface-v1): a sidebar with the
/// groups, a main column with the group identity, shortcuts and selected-shortcut cards, a
/// right column with the popup appearance card, and a sticky footer with the save state and
/// actions. Everything is themed from the app appearance (<see cref="IEditorThemeSource"/>)
/// and repaints live on a theme change without touching the edit buffer.
/// <para>
/// All editing happens on a private copy of the group; the store is only touched by Save and
/// Delete. Every successful change raises <see cref="GroupsChanged"/>, so the resident can
/// refresh its group list and icons for the very next popup without a restart. Leaving a group
/// with unsaved changes (another group, New group, closing the window) asks Save / Discard /
/// Cancel.
/// </para>
/// <para>Partial files: Layout (controls and responsive layout), Groups (sidebar, guard, import,
/// delete), Shortcuts (list, selection, dialogs), Save (save, taskbar shortcut), Rename.</para>
/// </summary>
internal sealed partial class EditorForm : Form
{
    private const string GroupIconFileName = "GroupIcon.ico";
    private const int MinTransparency = 10;
    private const int MaxTransparency = 90;

    private readonly GroupStore _store;
    private readonly bool _listGroups;
    private readonly PinnedStateCache _pins;
    private readonly IconCache? _icons;
    private readonly IEditorThemeSource _theme;
    private readonly IDisposable? _ownedTheme;
    private readonly string? _shortcutsDirectory;
    private readonly EntryListControl _entries;
    private readonly TextBox _name = new();
    private readonly TextBox _arguments = new();
    private readonly TextBox _workingDirectory = new();
    private readonly Label _status = new();
    private readonly IAppearanceSettings? _appearanceSettings;
    private readonly AppearancePage? _appearancePage;

    private Group _group;
    private string? _savedName;
    private Color _customColor = ColorTranslator.FromHtml(Group.DarkColorHex);
    private bool _loading;
    private bool _dirty;
    private bool _transparencyTouched;
    private bool _exitApproved;

    /// <summary>
    /// Creates the editor. <paramref name="listGroups"/> opens the first existing group (the
    /// "Edit groups..." entry point); otherwise the form starts as a new group with the legacy
    /// defaults. <paramref name="pinState"/> overrides the pin cache used by the "Create taskbar
    /// shortcut" handoff (tests inject a fake one). <paramref name="shortcutsDirectory"/>
    /// overrides where taskbar .lnk files are written (tests use a temp folder).
    /// <paramref name="icons"/> is the resident's icon cache, shared so rows load icons off the
    /// UI thread and reuse what the popup already extracted. <paramref name="theme"/> is the app
    /// appearance; standalone editors use the mint palette in the Windows mode.
    /// </summary>
    public EditorForm(
        GroupStore store,
        bool listGroups = false,
        PinnedStateCache? pinState = null,
        string? shortcutsDirectory = null,
        IconCache? icons = null,
        IEditorThemeSource? theme = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _icons = icons;
        _entries = new EntryListControl(icons);
        _iconWell = new IconWell(this);
        _nameField = new ThemedField(_name);
        _argumentsField = new ThemedField(_arguments);
        _workingDirectoryField = new ThemedField(_workingDirectory);
        _shortcutsDirectory = shortcutsDirectory;
        _listGroups = listGroups;
        _pins = pinState ?? CreatePinState();
        _group = new Group();
        if (theme is null)
        {
            var fallback = new StaticThemeSource(
                AppearancePreferences.DefaultPaletteId,
                Appearance.ThemeService.ReadSystemUsesLight() ? ResolvedMode.Light : ResolvedMode.Dark);
            _theme = fallback;
            _ownedTheme = fallback;
        }
        else
        {
            _theme = theme;
        }

        // The Appearance page needs writable app settings (the resident's ThemeService).
        _appearanceSettings = _theme as IAppearanceSettings;
        _appearancePage = _appearanceSettings is null ? null : new AppearancePage(_appearanceSettings, _theme);

        // The layout is written in 96-DPI pixels and converted with LogicalToDeviceUnits.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildUi();
        _theme.Changed += OnThemeChanged;
        ApplyTheme();

        RefreshSidebar(selectName: null);
        if (_listGroups && _sidebar.Items.Count > 0)
        {
            LoadGroupByName(_sidebar.Items[0].Name);
        }
        else
        {
            LoadGroupIntoControls();
        }
    }

    /// <summary>Raised after a successful Save, Delete or Import, so the resident can refresh its caches.</summary>
    public event EventHandler? GroupsChanged;

    /// <summary>Choice in the unsaved-changes prompt.</summary>
    internal enum GuardChoice
    {
        Save,
        Discard,
        Cancel,
    }

    /// <summary>Test hook: answers the unsaved-changes prompt instead of showing it.</summary>
    internal Func<string, GuardChoice>? GuardPromptOverride { get; set; }

    /// <summary>Test hook: answers the delete confirmation instead of showing it.</summary>
    internal Func<string, bool>? DeleteConfirmOverride { get; set; }

    /// <summary>
    /// Resident exit: runs the unsaved-changes prompt and closes the editor unless the user
    /// cancels. Returns false when the exit must not continue.
    /// </summary>
    internal bool TryCloseForExit()
    {
        if (!ConfirmLeaveDirty())
        {
            return false;
        }

        _exitApproved = true;
        Close();
        return true;
    }

    /// <summary>True while the edit buffer differs from what was loaded or last saved.</summary>
    internal bool IsDirty => _dirty;

    /// <summary>True while the Appearance page is shown instead of the group.</summary>
    internal bool ShowingAppearance => _showingAppearance;

    /// <summary>True while Delete / Create taskbar shortcut / Save are offered (group page).</summary>
    internal bool GroupActionsVisible => _groupActionsVisible;

    // Fields, not Control.Visible: Visible reads false until the form is first shown.
    private bool _showingAppearance;
    private bool _groupActionsVisible = true;

    // Resolved lazily so a test-supplied folder never touches the real data root.
    private string ShortcutsDirectory => _shortcutsDirectory ?? DataRootResolver.ForCurrentProcess().ShortcutsDirectory;

    /// <summary>
    /// Pin state for the "Create taskbar shortcut" handoff. The editor keeps its own cache
    /// (the resident's lives in TrayContext) and refreshes just the clicked group at click
    /// time; that IO runs on a user action, never on the popup show path.
    /// </summary>
    private static PinnedStateCache CreatePinState()
    {
        var dataRoot = DataRootResolver.ForCurrentProcess();
        return new PinnedStateCache(
            dataRoot.ShortcutsDirectory,
            PinnedStateCache.DefaultPinnedDirectory(),
            Application.ExecutablePath);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
        NativeTheme.ApplyScrollbars(_content, _theme.EffectiveMode);
        if (_appearancePage is not null)
        {
            NativeTheme.ApplyScrollbars(_appearancePage, _theme.EffectiveMode);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitApproved
            && e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall
            && !ConfirmLeaveDirty())
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        drgevent.Effect = GetDroppableFiles(drgevent).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragDrop(DragEventArgs drgevent)
    {
        base.OnDragDrop(drgevent);
        var files = GetDroppableFiles(drgevent);
        if (files.Count > 0)
        {
            AddTargets(files);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _theme.Changed -= OnThemeChanged;
            DisposeSidebarImages();
            _iconWell.GroupIcon?.Dispose();
            _toolTip.Dispose();
            _iconMenu.Dispose();
            _ownedTheme?.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- dirty state

    private void MarkDirty()
    {
        if (_loading)
        {
            return;
        }

        _dirty = true;
        UpdateFooter(StatusKind.Dirty, "Unsaved changes");
        UpdateHeading();
        UpdatePreview();
    }

    /// <summary>
    /// True when it is fine to leave the current group: nothing unsaved, or the user chose
    /// Save (and it succeeded) or Discard. Cancel, or a failed save, keeps the editor where it is.
    /// </summary>
    private bool ConfirmLeaveDirty()
    {
        if (!_dirty)
        {
            return true;
        }

        var name = string.IsNullOrWhiteSpace(_name.Text) ? "this new group" : $"'{_name.Text}'";
        var choice = GuardPromptOverride?.Invoke(name) ?? ShowGuardPrompt(name);
        switch (choice)
        {
            case GuardChoice.Save:
                SaveGroup();
                return !_dirty;
            case GuardChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    private GuardChoice ShowGuardPrompt(string name)
    {
        var save = new TaskDialogButton("Save");
        var discard = new TaskDialogButton("Don't save");
        var page = new TaskDialogPage
        {
            Caption = AppConstants.AppName,
            Heading = $"Save changes to {name}?",
            Text = "Your edits to this group have not been saved yet.",
            Buttons = { save, discard, TaskDialogButton.Cancel },
            DefaultButton = save,
            Icon = TaskDialogIcon.Warning,
        };

        var result = TaskDialog.ShowDialog(this, page);
        return result == save ? GuardChoice.Save : result == discard ? GuardChoice.Discard : GuardChoice.Cancel;
    }

    // ---------------------------------------------------------------- theme

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(ApplyTheme));
        }
        else
        {
            ApplyTheme();
        }
    }

    /// <summary>Repaints everything from the current tokens; the edit buffer is untouched.</summary>
    private void ApplyTheme()
    {
        var tokens = _theme.Tokens;
        var mode = _theme.EffectiveMode;
        Icon = _theme.GetAppIcon(SystemInformation.IconSize);
        BackColor = tokens.Canvas;
        _content.BackColor = tokens.Canvas;
        _sidebar.SetLogo(_theme.PaletteId);
        _preview.LogoPalette = _theme.PaletteId;
        ApplyThemeRecursive(this, tokens, mode);
        ApplyLabelColors(tokens);
        _iconWell.Invalidate();
        RefreshSidebarImagesForTheme();
        ApplyTitleBarTheme();
        NativeTheme.ApplyScrollbars(_content, mode);
        if (_appearancePage is not null)
        {
            NativeTheme.ApplyScrollbars(_appearancePage, mode);
        }

        Invalidate(true);
    }

    private static void ApplyThemeRecursive(Control parent, ThemeTokens tokens, ResolvedMode mode)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is IThemedControl themed)
            {
                themed.ApplyTheme(tokens, mode);
            }

            // EntryListControl themes its own rows.
            if (child is not EntryListControl)
            {
                ApplyThemeRecursive(child, tokens, mode);
            }
        }
    }

    /// <summary>Dark caption in dark mode and the palette's title-bar colour (Windows 11).</summary>
    private void ApplyTitleBarTheme()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var tokens = _theme.Tokens;
        var dark = _theme.EffectiveMode == ResolvedMode.Dark ? 1 : 0;
        var caption = ColorRef(tokens.Titlebar);
        var text = ColorRef(tokens.Text);
        try
        {
            _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, DwmwaCaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(Handle, DwmwaTextColor, ref text, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: the default caption stays.
        }
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
