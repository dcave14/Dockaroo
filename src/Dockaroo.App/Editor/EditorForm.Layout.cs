using System.Drawing;
using System.Windows.Forms;
using Dockaroo.App.Appearance;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor;

/// <summary>
/// Controls and responsive layout (design-spec.json). Reference 1440x960: 240px sidebar, 24px
/// gutters, a 696px main column and a 432px right column, 72px sticky footer. Below 1280px wide
/// the sidebar narrows to 208px and the columns stack in one scrolling area; the footer always
/// stays visible. All numbers are 96-DPI pixels, converted with LogicalToDeviceUnits.
/// </summary>
internal sealed partial class EditorForm
{
    // Spec: stack the columns below 1280 logical px of window width.
    private const int WideLayoutMinWidth = 1280;

    private readonly EditorSidebar _sidebar = new();
    private readonly Panel _content = new() { AutoScroll = true };
    private readonly FooterPanel _footer = new();
    private readonly Label _title = new();
    private readonly Label _subtitle = new();

    private readonly CardPanel _identityCard = new("Group identity");
    private readonly IconWell _iconWell;
    private readonly Label _nameLabel = new() { Text = "Group name" };
    private readonly Label _nameError = new();
    private readonly ThemedField _nameField;

    private readonly CardPanel _shortcutsCard = new("Shortcuts");
    private readonly Label _shortcutCount = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly ThemedButton _addShortcut = new("Add shortcut", ButtonKind.Primary, Glyphs.Add);
    private readonly Label _shortcutError = new();

    private readonly CardPanel _selectedCard = new("Selected shortcut");
    private readonly Label _selectedSummary = new() { AutoEllipsis = true };
    private readonly Label _argumentsLabel = new() { Text = "Arguments" };
    private readonly Label _workingDirectoryLabel = new() { Text = "Working directory" };
    private readonly ThemedField _argumentsField;
    private readonly ThemedField _workingDirectoryField;
    private readonly ThemedButton _browse = new("Browse", ButtonKind.Secondary);

    private readonly CardPanel _previewCard = new("Taskbar preview");
    private readonly TaskbarPreview _preview = new();
    private readonly Label _previewCaption = new();

    private readonly CardPanel _popupCard = new("Popup appearance");
    private readonly Label _appsPerRowLabel = new() { Text = "Apps per row" };
    private readonly Stepper _width = new() { Minimum = Group.MinWidth, Maximum = Group.MaxWidth, AccessibleName = "Apps per row" };
    private readonly Label _backgroundLabel = new() { Text = "Popup background" };
    private readonly SegmentedControl _background = new() { AccessibleName = "Popup background" };
    private readonly Label _transparencyLabel = new() { Text = "Transparency" };
    private readonly Label _transparencyValue = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly TransparencySlider _transparency = new() { Minimum = MinTransparency, Maximum = MaxTransparency, Step = 10, AccessibleName = "Transparency" };
    private readonly Label _moreSolid = new() { Text = "More solid" };
    private readonly Label _moreTransparent = new() { Text = "More transparent", TextAlign = ContentAlignment.TopRight };
    private readonly Panel _popupSeparator = new();
    private readonly Label _allowOpenAllLabel = new() { Text = "Allow open all" };
    private readonly Label _allowOpenAllHint = new() { Text = "Ctrl + Enter launches this group" };
    private readonly ToggleSwitch _allowOpenAll = new() { AccessibleName = "Allow open all" };

    private readonly Label _statusGlyph = new() { TextAlign = ContentAlignment.MiddleCenter };
    private readonly ThemedButton _delete = new("Delete group", ButtonKind.Danger, Glyphs.Delete);
    private readonly ThemedButton _createShortcut = new("Create taskbar shortcut", ButtonKind.Secondary);
    private readonly ThemedButton _showInExplorer = new("Show in File Explorer", ButtonKind.Secondary);

    // Drag-to-pin card shown above the footer after "Create taskbar shortcut".
    private readonly PinChipCard _pinChip = new();
    private readonly ThemedButton _save = new("Save changes", ButtonKind.Primary);
    private readonly ToolTip _toolTip = new();

    // Label -> colour role, applied on every theme change.
    private readonly List<(Label Label, LabelRole Role)> _labelRoles = new();
    private StatusKind _statusKind;

    private enum LabelRole
    {
        Title,
        Text,
        Muted,
        Faint,
        Danger,
        Accent,
    }

    private enum StatusKind
    {
        None,
        Dirty,
        Saved,
        Info,
        Error,
    }

    private void BuildUi()
    {
        Text = "Edit group";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 920);
        MinimumSize = new Size(960, 720);
        AllowDrop = true;
        KeyPreview = true;

        ShrinkToWorkingArea();

        // Sidebar
        _sidebar.GroupClicked += (_, index) => OnSidebarGroupClicked(index);
        _sidebar.NewGroupClicked += (_, _) => StartNewGroup();
        _sidebar.ImportClicked += (_, _) => ImportLegacyGroups();

        // Heading
        Role(_title, LabelRole.Title, EditorFonts.Title);
        Role(_subtitle, LabelRole.Muted, EditorFonts.Body);
        _title.AutoEllipsis = true;
        _content.Controls.Add(_title);
        _content.Controls.Add(_subtitle);

        BuildIdentityCard();
        BuildShortcutsCard();
        BuildSelectedCard();
        BuildPreviewCard();
        BuildPopupCard();
        BuildFooter();

        _content.Controls.Add(_identityCard);
        _content.Controls.Add(_shortcutsCard);
        _content.Controls.Add(_selectedCard);
        _content.Controls.Add(_previewCard);
        _content.Controls.Add(_popupCard);
        _content.Resize += (_, _) => LayoutContent();

        Controls.Add(_content);
        if (_appearancePage is not null)
        {
            _appearancePage.Visible = false;
            _appearancePage.ChangeAttempted += (_, result) =>
                UpdateFooter(result.Saved ? StatusKind.Saved : StatusKind.Error, result.Text);
            Controls.Add(_appearancePage);
            _sidebar.ShowAppearance = true;
            _sidebar.AppearanceClicked += (_, _) => ShowAppearancePage();
        }

        Controls.Add(_pinChip);
        Controls.Add(_footer);
        Controls.Add(_sidebar);
        AcceptButton = _save;
    }

    private void BuildIdentityCard()
    {
        _identityCard.Controls.Add(_iconWell);
        Role(_nameLabel, LabelRole.Muted, EditorFonts.Label);
        Role(_nameError, LabelRole.Danger, EditorFonts.Label);
        _nameError.AutoEllipsis = true;
        _nameError.Visible = false;
        _name.MaxLength = Group.MaxNameLength;
        _name.AccessibleName = "Group name";
        _name.PlaceholderText = "Name this group";
        _name.TextChanged += (_, _) =>
        {
            ClearNameError();
            MarkDirty();
            UpdateHeading();
        };
        _identityCard.Controls.Add(_nameLabel);
        _identityCard.Controls.Add(_nameError);
        _identityCard.Controls.Add(_nameField);
        BuildIconPicker();
        _identityCard.Layout += (_, _) =>
        {
            var w = _identityCard.Width;
            _iconWell.Bounds = Rect(20, 52, 56, 56);
            _nameLabel.Bounds = Rect(96, 50, 110, 16);
            _nameError.Bounds = new Rectangle(L(212), L(50), Math.Max(0, w - L(232)), L(16));
            _changeIcon.Bounds = new Rectangle(w - L(20) - L(160), L(68), L(160), L(40));
            _nameField.Bounds = new Rectangle(L(96), L(68), Math.Max(L(120), _changeIcon.Left - L(12) - L(96)), L(40));
        };
    }

    private void BuildShortcutsCard()
    {
        Role(_shortcutCount, LabelRole.Muted, EditorFonts.Label);
        Role(_shortcutError, LabelRole.Danger, EditorFonts.Label);
        _shortcutError.Visible = false;
        _shortcutError.AutoEllipsis = true;
        _addShortcut.Click += (_, _) => AddShortcutsViaDialog();
        _entries.SelectionChanged += (_, _) => UpdateSelectionPanel();
        _entries.EntriesChanged += (_, _) =>
        {
            _shortcutError.Visible = false;
            MarkDirty();
            UpdateShortcutCount();
        };
        _entries.AddRequested += (_, _) => AddShortcutsViaDialog();
        _entries.IconLoaded += (_, _) => _preview.Invalidate();
        _shortcutsCard.Controls.Add(_shortcutCount);
        _shortcutsCard.Controls.Add(_addShortcut);
        _shortcutsCard.Controls.Add(_shortcutError);
        _shortcutsCard.Controls.Add(_entries);
        _shortcutsCard.Layout += (_, _) =>
        {
            var w = _shortcutsCard.Width;
            var h = _shortcutsCard.Height;
            _addShortcut.Bounds = new Rectangle(w - L(20) - L(152), L(14), L(152), L(40));
            _shortcutCount.Bounds = new Rectangle(_addShortcut.Left - L(96), L(14), L(80), L(40));
            // Spec: rows start 64px into the card, 16px bottom padding (4 rows + drop area at 360px).
            _shortcutError.Bounds = new Rectangle(L(20), L(46), Math.Max(0, w - L(40)), L(16));
            _entries.Bounds = new Rectangle(L(20), L(64), Math.Max(0, w - L(40)), Math.Max(0, h - L(80)));
        };
    }

    private void BuildSelectedCard()
    {
        Role(_selectedSummary, LabelRole.Muted, EditorFonts.Label);
        Role(_argumentsLabel, LabelRole.Muted, EditorFonts.Label);
        Role(_workingDirectoryLabel, LabelRole.Muted, EditorFonts.Label);
        _arguments.AccessibleName = "Arguments";
        _workingDirectory.AccessibleName = "Working directory";
        _arguments.TextChanged += (_, _) =>
        {
            if (!_loading && _entries.SelectedEntry is { } entry && entry.Arguments != _arguments.Text)
            {
                entry.Arguments = _arguments.Text;
                MarkDirty();
            }
        };
        _workingDirectory.TextChanged += (_, _) =>
        {
            if (!_loading && _entries.SelectedEntry is { } entry && entry.WorkingDirectory != _workingDirectory.Text)
            {
                entry.WorkingDirectory = _workingDirectory.Text;
                MarkDirty();
            }
        };
        _browse.Click += (_, _) => BrowseWorkingDirectory();
        _selectedCard.Controls.AddRange(new Control[] { _selectedSummary, _argumentsLabel, _argumentsField, _workingDirectoryLabel, _workingDirectoryField, _browse });
        _selectedCard.Layout += (_, _) =>
        {
            var w = _selectedCard.Width;
            _selectedSummary.Bounds = new Rectangle(L(196), L(22), Math.Max(0, w - L(216)), L(24));
            _argumentsLabel.Bounds = Rect(20, 66, 132, 20);
            _argumentsField.Bounds = new Rectangle(L(156), L(56), Math.Max(L(120), w - L(176)), L(40));
            _workingDirectoryLabel.Bounds = Rect(20, 118, 132, 20);
            _browse.Bounds = new Rectangle(w - L(20) - L(96), L(108), L(96), L(40));
            _workingDirectoryField.Bounds = new Rectangle(L(156), L(108), Math.Max(L(80), _browse.Left - L(12) - L(156)), L(40));
        };
    }

    private void BuildPreviewCard()
    {
        Role(_previewCaption, LabelRole.Muted, EditorFonts.Label);
        _previewCard.Controls.Add(_preview);
        _previewCard.Controls.Add(_previewCaption);
        _previewCard.Layout += (_, _) =>
        {
            var w = _previewCard.Width;
            var h = _previewCard.Height;
            _preview.Bounds = new Rectangle(L(24), L(52), Math.Max(0, w - L(48)), Math.Max(0, h - L(52) - L(64)));
            _previewCaption.Bounds = new Rectangle(L(24), h - L(48), Math.Max(0, w - L(48)), L(24));
        };
    }

    /// <summary>Pushes the edit buffer (not the saved group) into the taskbar preview.</summary>
    private void UpdatePreview()
    {
        var count = _entries.Entries.Count;
        var transparency = _transparencyTouched ? _transparency.Value : _group.Opacity;

        // Same column fit as the real popup on the screen the editor is on.
        var perRow = count == 0
            ? _width.Value
            : Dockaroo.Core.Popup.PopupGeometry.FitPerRow(count, _width.Value, Screen.FromControl(this).WorkingArea, Dockaroo.Core.Popup.PopupMetrics.ForDpi(DeviceDpi));
        _preview.SetState(new PreviewState(
            count,
            perRow,
            CurrentColorHex(),
            transparency,
            i => _entries.RowImage(i) as Bitmap,
            i => i < _entries.Entries.Count ? _entries.Entries[i].Name : string.Empty));
        _previewCaption.Text = TaskbarPreview.Caption(count, perRow);
    }

    private void BuildPopupCard()
    {
        Role(_appsPerRowLabel, LabelRole.Text, EditorFonts.Body);
        Role(_backgroundLabel, LabelRole.Muted, EditorFonts.Label);
        Role(_transparencyLabel, LabelRole.Text, EditorFonts.Body);
        Role(_transparencyValue, LabelRole.Accent, EditorFonts.Body);
        Role(_moreSolid, LabelRole.Faint, EditorFonts.Micro);
        Role(_moreTransparent, LabelRole.Faint, EditorFonts.Micro);
        Role(_allowOpenAllLabel, LabelRole.Text, EditorFonts.Body);
        Role(_allowOpenAllHint, LabelRole.Muted, EditorFonts.Label);

        _background.Segments.Add(new Segment("Dark", Glyphs.Moon));
        _background.Segments.Add(new Segment("Light", Glyphs.Sun));
        _background.Segments.Add(new Segment("Custom", Glyphs.Color));
        _background.SwatchIndex = 2;
        _background.SelectedIndexChanged += (_, _) => MarkDirty();
        _background.SegmentClicked += (_, index) =>
        {
            if (index == 2 && !_loading)
            {
                PickCustomColor();
            }
        };

        _width.ValueChanged += (_, _) => MarkDirty();
        _transparency.ValueChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _transparencyTouched = true;
            _transparencyValue.Text = $"{_transparency.Value}%";
            MarkDirty();
        };
        _allowOpenAll.CheckedChanged += (_, _) => MarkDirty();

        _popupCard.Controls.AddRange(new Control[]
        {
            _appsPerRowLabel, _width, _backgroundLabel, _background, _transparencyLabel, _transparencyValue,
            _transparency, _moreSolid, _moreTransparent, _popupSeparator, _allowOpenAllLabel, _allowOpenAllHint, _allowOpenAll,
        });
        _popupCard.Layout += (_, _) =>
        {
            var w = _popupCard.Width;
            var inner = Math.Max(0, w - L(40));
            _appsPerRowLabel.Bounds = Rect(20, 74, 160, 24);
            _width.Bounds = new Rectangle(w - L(20) - L(136), L(66), L(136), L(40));
            _backgroundLabel.Bounds = Rect(20, 118, 200, 18);
            _background.Bounds = new Rectangle(L(20), L(140), inner, L(40));
            _transparencyLabel.Bounds = Rect(20, 202, 160, 24);
            _transparencyValue.Bounds = new Rectangle(w - L(20) - L(80), L(202), L(80), L(24));
            _transparency.Bounds = new Rectangle(L(20), L(230), inner, L(22));
            _moreSolid.Bounds = Rect(20, 256, 140, 16);
            _moreTransparent.Bounds = new Rectangle(w - L(20) - L(160), L(256), L(160), L(16));
            _popupSeparator.Bounds = new Rectangle(L(20), L(290), inner, 1);
            _allowOpenAllLabel.Bounds = Rect(20, 306, 200, 24);
            _allowOpenAllHint.Bounds = Rect(20, 332, 260, 18);
            _allowOpenAll.Bounds = new Rectangle(w - L(20) - L(44), L(310), L(44), L(26));
        };
    }

    private void BuildFooter()
    {
        _statusGlyph.Font = EditorFonts.IconSmall;
        _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = EditorFonts.Label;
        _delete.Click += (_, _) => DeleteGroup();
        _createShortcut.Click += (_, _) => CreateTaskbarShortcut();
        _showInExplorer.Click += (_, _) => ShowShortcutInExplorer();
        _save.Click += (_, _) => SaveGroup();
        _footer.Controls.AddRange(new Control[] { _statusGlyph, _status, _delete, _showInExplorer, _createShortcut, _save });
        _footer.Layout += (_, _) =>
        {
            var w = _footer.Width;
            var h = _footer.Height;
            var wide = Width >= L(WideLayoutMinWidth);
            _save.Bounds = new Rectangle(w - L(24) - L(160), (h - L(44)) / 2, L(160), L(44));
            var createWidth = wide ? L(240) : L(216);
            _createShortcut.Bounds = new Rectangle(_save.Left - L(12) - createWidth, (h - L(44)) / 2, createWidth, L(44));
            var showWidth = wide ? L(200) : L(184);
            _showInExplorer.Bounds = new Rectangle(_createShortcut.Left - L(12) - showWidth, (h - L(44)) / 2, showWidth, L(44));
            // Delete sits at the left edge, apart from the actions on the right; the save status
            // follows it and takes whatever room is left before the right-hand buttons.
            _delete.Bounds = new Rectangle(L(24), (h - L(40)) / 2, L(136), L(40));
            // The field, not _delete.Visible: Visible reads false until the form is first shown.
            var statusLeft = _groupActionsVisible ? _delete.Right + L(20) : L(20);
            // Start below the 1px top border so the labels never paint over it.
            _statusGlyph.Bounds = new Rectangle(statusLeft, 1, L(20), h - 1);
            _status.Bounds = new Rectangle(statusLeft + L(24), 1, Math.Max(0, _showInExplorer.Left - L(12) - (statusLeft + L(24))), h - 1);
            PositionPinChip();
        };
    }

    /// <summary>
    /// The pin card sits on top of "Create taskbar shortcut": same width and edges, its bottom
    /// touching the button's top, so it reads as the button's own popover.
    /// </summary>
    private void PositionPinChip()
    {
        var button = _createShortcut.Bounds;
        var height = L(PinChipCard.LogicalHeight);
        _pinChip.Bounds = new Rectangle(_footer.Left + button.Left, _footer.Top + button.Top - height, button.Width, height);
    }

    /// <summary>Starts at the reference size but never larger than the screen it opens on.</summary>
    private void ShrinkToWorkingArea()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var size = SizeFromClientSize(ClientSize);
        if (size.Width > area.Width || size.Height > area.Height)
        {
            Size = new Size(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutShell();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyTheme();
        LayoutShell();
    }

    private void LayoutShell()
    {
        if (_footer is null || _content is null || _sidebar is null)
        {
            return;
        }

        var client = ClientSize;
        var wide = Width >= L(WideLayoutMinWidth);
        var sidebarWidth = wide ? L(240) : L(208);
        var footerHeight = L(72);
        _sidebar.Bounds = new Rectangle(0, 0, sidebarWidth, client.Height);
        _footer.Bounds = new Rectangle(sidebarWidth, client.Height - footerHeight, Math.Max(0, client.Width - sidebarWidth), footerHeight);
        _content.Bounds = new Rectangle(sidebarWidth, 0, Math.Max(0, client.Width - sidebarWidth), Math.Max(0, client.Height - footerHeight));
        if (_appearancePage is not null)
        {
            _appearancePage.Bounds = _content.Bounds;
        }

        PositionPinChip();
        LayoutContent();
    }

    /// <summary>Positions the heading and cards inside the scrolling content area.</summary>
    private void LayoutContent()
    {
        var viewport = _content.ClientSize;
        if (viewport.Width <= 0)
        {
            return;
        }

        var wide = Width >= L(WideLayoutMinWidth);
        var gutter = L(24);
        var scroll = _content.AutoScrollPosition;
        int total;

        if (wide)
        {
            var rightWidth = L(432);
            // No floor above what 1280px leaves (~500px), so the right column never overflows.
            var mainWidth = Math.Max(L(400), viewport.Width - (3 * gutter) - rightWidth);
            var rightX = gutter + mainWidth + gutter;
            var shortcutsHeight = Math.Max(L(260), viewport.Height - L(272) - L(24 + 168) - gutter);
            PlaceHeading(gutter, L(32), mainWidth, scroll);
            _identityCard.Bounds = Offset(new Rectangle(gutter, L(120), mainWidth, L(128)), scroll);
            _shortcutsCard.Bounds = Offset(new Rectangle(gutter, L(272), mainWidth, shortcutsHeight), scroll);
            _selectedCard.Bounds = Offset(new Rectangle(gutter, L(272) + shortcutsHeight + gutter, mainWidth, L(168)), scroll);
            _previewCard.Bounds = Offset(new Rectangle(rightX, L(40), rightWidth, L(376)), scroll);
            _popupCard.Bounds = Offset(new Rectangle(rightX, L(440), rightWidth, L(384)), scroll);
            total = Math.Max(L(272) + shortcutsHeight + gutter + L(168), L(440) + L(384)) + gutter;
        }
        else
        {
            var width = Math.Max(L(400), viewport.Width - (2 * gutter));
            var y = L(24);
            PlaceHeading(gutter, y, width, scroll);
            y += L(88);
            _identityCard.Bounds = Offset(new Rectangle(gutter, y, width, L(128)), scroll);
            y += L(128) + gutter;
            _shortcutsCard.Bounds = Offset(new Rectangle(gutter, y, width, L(360)), scroll);
            y += L(360) + gutter;
            _selectedCard.Bounds = Offset(new Rectangle(gutter, y, width, L(168)), scroll);
            y += L(168) + gutter;
            _previewCard.Bounds = Offset(new Rectangle(gutter, y, width, L(376)), scroll);
            y += L(376) + gutter;
            _popupCard.Bounds = Offset(new Rectangle(gutter, y, width, L(384)), scroll);
            total = y + L(384) + gutter;
        }

        var minSize = new Size(0, total);
        if (_content.AutoScrollMinSize != minSize)
        {
            _content.AutoScrollMinSize = minSize;
        }
    }

    private void PlaceHeading(int x, int y, int width, Point scroll)
    {
        _title.Bounds = Offset(new Rectangle(x, y, width, L(48)), scroll);
        _subtitle.Bounds = Offset(new Rectangle(x, y + L(48), width, L(22)), scroll);
    }

    private static Rectangle Offset(Rectangle bounds, Point scroll) => new(bounds.X + scroll.X, bounds.Y + scroll.Y, bounds.Width, bounds.Height);

    private int L(int logical) => LogicalToDeviceUnits(logical);

    private Rectangle Rect(int x, int y, int width, int height) => new(L(x), L(y), L(width), L(height));

    private void Role(Label label, LabelRole role, Font font)
    {
        label.Font = font;
        label.UseMnemonic = false;
        _labelRoles.Add((label, role));
    }

    private void ApplyLabelColors(ThemeTokens t)
    {
        foreach (var (label, role) in _labelRoles)
        {
            label.ForeColor = role switch
            {
                LabelRole.Title or LabelRole.Text => t.Text,
                LabelRole.Muted => t.Muted,
                LabelRole.Faint => t.Faint,
                LabelRole.Danger => t.Danger,
                _ => t.Primary,
            };
            label.BackColor = label.Parent?.BackColor ?? t.Canvas;
        }

        ApplySelectionLabelColors();
        _popupSeparator.BackColor = t.Border;
        _footer.ApplyTheme(t, _theme.EffectiveMode);
        _status.BackColor = t.Surface;
        _statusGlyph.BackColor = t.Surface;
        UpdateFooter(_statusKind, _status.Text);
    }

    // ---------------------------------------------------------------- heading and footer

    private void UpdateHeading()
    {
        _title.Text = string.IsNullOrWhiteSpace(_name.Text) ? "New group" : _name.Text;
        UpdateShortcutCount();
    }

    private void UpdateShortcutCount()
    {
        var count = _entries.Entries.Count;
        _subtitle.Text = count == 1 ? "1 shortcut" : $"{count} shortcuts";
        _shortcutCount.Text = $"{count} / {EntryListControl.MaxEntries}";
        _addShortcut.Enabled = count < EntryListControl.MaxEntries;
    }

    private void UpdateFooter(StatusKind kind, string text)
    {
        _statusKind = kind;
        _status.Text = text;
        _toolTip.SetToolTip(_status, text);
        var t = _theme.Tokens;
        (_statusGlyph.Text, _statusGlyph.ForeColor) = kind switch
        {
            StatusKind.Dirty => ("\u25CF", t.Warning),
            StatusKind.Saved => (Glyphs.CheckMark, t.Primary),
            StatusKind.Error => ("\u25CF", t.Danger),
            StatusKind.Info => ("\u25CF", t.Primary),
            _ => (string.Empty, t.Muted),
        };
        _statusGlyph.Font = kind == StatusKind.Saved ? EditorFonts.IconSmall : EditorFonts.Micro;
        _status.ForeColor = kind == StatusKind.Error ? t.Danger : t.Muted;
        _save.Enabled = _dirty;
        _delete.Enabled = _savedName is not null;
    }

    /// <summary>The group's icon in the identity card: GroupIcon.ico when present, else the app logo.</summary>
    private sealed class IconWell : Control
    {
        private readonly EditorForm _owner;

        public IconWell(EditorForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public Icon? GroupIcon { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var t = _owner._theme.Tokens;
            Draw.RoundedBox(e.Graphics, ClientRectangle, LogicalToDeviceUnits(12), t.IconWell);
            var size = LogicalToDeviceUnits(40);
            var box = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            if (GroupIcon is { } icon)
            {
                e.Graphics.DrawIcon(icon, box);
            }
            else
            {
                e.Graphics.DrawImage(PaletteLogos.Square(_owner._theme.PaletteId, size), box);
            }
        }
    }

    /// <summary>The sticky footer: surface fill with a 1px top border.</summary>
    private sealed class FooterPanel : Panel, IThemedControl
    {
        private Color _border = SystemColors.ControlDark;

        public FooterPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
        {
            BackColor = tokens.Surface;
            _border = tokens.Border;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(_border);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }
}
