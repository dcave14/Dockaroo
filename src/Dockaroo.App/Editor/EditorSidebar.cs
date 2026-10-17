using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Dockaroo.App.Appearance;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor;

/// <summary>One group in the sidebar list. <see cref="Image"/> is owned by the caller.</summary>
internal sealed record SidebarItem(string Name, Image? Image);

/// <summary>
/// The editor's left navigation (design spec, 240px; 208px in the compact layout): brand logo
/// and name, "YOUR GROUPS" with one row per group, New group, and Import groups at the bottom.
/// Clicking a group raises <see cref="GroupClicked"/>; the editor decides (unsaved-changes guard)
/// whether the selection actually moves, then sets <see cref="SelectedIndex"/>.
/// </summary>
internal sealed class EditorSidebar : Panel, IThemedControl
{
    private readonly GroupList _list;
    private readonly ThemedButton _newGroup;
    private readonly ThemedButton _import;
    private readonly ThemedButton _appearance;
    private bool _showAppearance;
    private ThemeTokens? _tokens;
    private const TextFormatFlags BrandFlags = TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

    // Ink rows per (font, text): measured once, the brand text and fonts are fixed.
    private static readonly Dictionary<(Font Font, string Text), (int Top, int Bottom, int LineHeight)> InkCache = new();

    private string? _logoPalette;

    public EditorSidebar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _list = new GroupList(this);
        _newGroup = new ThemedButton("New group", ButtonKind.Nav, Glyphs.Add);
        _newGroup.Click += (_, _) => NewGroupClicked?.Invoke(this, EventArgs.Empty);
        _import = new ThemedButton("Import groups", ButtonKind.Nav, Glyphs.Download) { AccessibleName = "Import groups from the old version" };
        _import.Click += (_, _) => ImportClicked?.Invoke(this, EventArgs.Empty);
        _appearance = new ThemedButton("Appearance", ButtonKind.Nav, Glyphs.Color) { Visible = false };
        _appearance.Click += (_, _) => AppearanceClicked?.Invoke(this, EventArgs.Empty);
        Controls.Add(_list);
        Controls.Add(_newGroup);
        Controls.Add(_appearance);
        Controls.Add(_import);
    }

    public event EventHandler<int>? GroupClicked;

    public event EventHandler? NewGroupClicked;

    public event EventHandler? ImportClicked;

    public event EventHandler? AppearanceClicked;

    /// <summary>Shows the Appearance navigation item (only when app settings are writable).</summary>
    public bool ShowAppearance
    {
        // A field, not Control.Visible: Visible reads false until the form is shown.
        get => _showAppearance;
        set
        {
            _showAppearance = value;
            _appearance.Visible = value;
            LayoutChildren();
        }
    }

    /// <summary>Highlights the Appearance item while its page is open.</summary>
    public bool AppearanceSelected
    {
        get => _appearance.IsSelected;
        set => _appearance.IsSelected = value;
    }

    /// <summary>Highlighted group row, or -1 (a new, unsaved group).</summary>
    public int SelectedIndex
    {
        get => _list.SelectedIndex;
        set => _list.SelectedIndex = value;
    }

    public IReadOnlyList<SidebarItem> Items => _list.Items;

    /// <summary>Brand text next to the logo.</summary>
    public string BrandText { get; set; } = AppConstants.AppName;

    /// <summary>Test hook: the New group button.</summary>
    internal ThemedButton NewGroupButton => _newGroup;

    public void SetItems(IReadOnlyList<SidebarItem> items, int selectedIndex)
    {
        _list.SetItems(items, selectedIndex);
        LayoutChildren();
    }

    /// <summary>Shows <paramref name="paletteId"/>'s logo next to the brand text.</summary>
    public void SetLogo(string paletteId)
    {
        _logoPalette = paletteId;
        Invalidate();
    }

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        BackColor = tokens.Sidebar;
        _list.BackColor = tokens.Sidebar;
        _newGroup.ApplyTheme(tokens, mode);
        _import.ApplyTheme(tokens, mode);
        _appearance.ApplyTheme(tokens, mode);
        _list.Invalidate();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutChildren();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_tokens is not { } t)
        {
            e.Graphics.Clear(SystemColors.Control);
            return;
        }

        var stops = t.SidebarGradient;
        using var brush = new LinearGradientBrush(ClientRectangle, stops[0], stops[^1], LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_tokens is not { } t)
        {
            return;
        }

        var g = e.Graphics;
        var pad = LogicalToDeviceUnits(20);
        // The logo is trimmed to its art, so its height is the brand row's height. The compact
        // (208px) sidebar uses a smaller one so the product name keeps its room.
        var logoHeight = Width < LogicalToDeviceUnits(230) ? LogicalToDeviceUnits(44) : LogicalToDeviceUnits(60);
        var top = LogicalToDeviceUnits(46) - (logoHeight / 2);
        var logoWidth = 0;
        if (_logoPalette is not null)
        {
            var logo = PaletteLogos.Fitted(_logoPalette, logoHeight);
            logoWidth = logo.Width;
            g.DrawImage(logo, new Rectangle(pad, top, logo.Width, logo.Height));
        }

        // The brand font steps down when the name does not fit the sidebar (compact layout).
        var brandLeft = pad + logoWidth + LogicalToDeviceUnits(12);
        var brandWidth = Width - brandLeft - LogicalToDeviceUnits(12);
        var brandFont = new[] { EditorFonts.Brand, EditorFonts.BodyStrong, EditorFonts.LabelStrong }
            .FirstOrDefault(font => TextRenderer.MeasureText(g, BrandText, font, Size.Empty, BrandFlags).Width <= brandWidth) ?? EditorFonts.LabelStrong;
        // Centre the letters' ink (not the font's line box) on the logo's visual middle.
        var (inkTop, inkBottom, lineHeight) = InkRows(BrandText, brandFont);
        var logoMiddle = top + (int)Math.Round(logoHeight * PaletteLogos.VisualCentre);
        var brandTop = logoMiddle - ((inkTop + inkBottom + 1) / 2);
        TextRenderer.DrawText(g, BrandText, brandFont, new Rectangle(brandLeft, brandTop, brandWidth, lineHeight), t.NavText, BrandFlags);
        TextRenderer.DrawText(
            g,
            "YOUR GROUPS",
            EditorFonts.Micro,
            new Rectangle(LogicalToDeviceUnits(24), LogicalToDeviceUnits(104), Width - LogicalToDeviceUnits(48), LogicalToDeviceUnits(16)),
            t.NavMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        using var line = new Pen(t.NavLine);
        var listBottom = _list.Bottom + LogicalToDeviceUnits(12);
        g.DrawLine(line, LogicalToDeviceUnits(24), listBottom, Width - LogicalToDeviceUnits(24), listBottom);
        var bottomGroupTop = (_showAppearance ? _appearance.Top : _import.Top) - LogicalToDeviceUnits(14);
        g.DrawLine(line, LogicalToDeviceUnits(24), bottomGroupTop, Width - LogicalToDeviceUnits(24), bottomGroupTop);
    }

    /// <summary>
    /// List from y=128 (logical) down to just above New group; New group follows the list (or
    /// sits above Import when the list fills the space); Import is pinned to the bottom.
    /// </summary>
    /// <summary>
    /// The first and last pixel rows that <paramref name="text"/> actually inks when drawn with
    /// <see cref="BrandFlags"/> at y=0, plus the line height; rendered once offscreen.
    /// </summary>
    internal static (int Top, int Bottom, int LineHeight) InkRows(string text, Font font)
    {
        if (InkCache.TryGetValue((font, text), out var rows))
        {
            return rows;
        }

        var size = TextRenderer.MeasureText(text, font, Size.Empty, BrandFlags);
        using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Black);
            TextRenderer.DrawText(g, text, font, new Rectangle(Point.Empty, bitmap.Size), Color.White, Color.Black, BrandFlags);
        }

        int top = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).G > 96)
                {
                    top = top < 0 ? y : top;
                    bottom = y;
                    break;
                }
            }
        }

        // No ink (empty text): treat the whole line as ink, which is plain line-box centring.
        rows = top < 0 ? (0, size.Height - 1, size.Height) : (top, bottom, size.Height);
        InkCache[(font, text)] = rows;
        return rows;
    }

    private void LayoutChildren()
    {
        var side = LogicalToDeviceUnits(20);
        var buttonHeight = LogicalToDeviceUnits(44);
        var importTop = Height - LogicalToDeviceUnits(20) - buttonHeight;
        _import.Bounds = new Rectangle(side, importTop, Width - (2 * side), buttonHeight);
        var bottomTop = importTop;
        if (_showAppearance)
        {
            bottomTop = importTop - LogicalToDeviceUnits(8) - buttonHeight;
            _appearance.Bounds = new Rectangle(side, bottomTop, Width - (2 * side), buttonHeight);
        }

        var listTop = LogicalToDeviceUnits(128);
        var maxListHeight = Math.Max(0, bottomTop - LogicalToDeviceUnits(28) - buttonHeight - LogicalToDeviceUnits(28) - listTop);
        var listHeight = Math.Min(_list.ContentHeight, maxListHeight);
        _list.Bounds = new Rectangle(0, listTop, Width, listHeight);
        _newGroup.Bounds = new Rectangle(side, _list.Bottom + LogicalToDeviceUnits(28), Width - (2 * side), buttonHeight);
        Invalidate();
    }

    /// <summary>The owner-drawn, scrollable, keyboard-navigable group rows.</summary>
    private sealed class GroupList : ScrollableControl
    {
        private const int ItemHeight = 52;
        private const int ItemPitch = 60;

        private readonly EditorSidebar _owner;
        private List<SidebarItem> _items = new();
        private int _selectedIndex = -1;
        private int _hoverIndex = -1;
        private int _focusIndex;

        public GroupList(EditorSidebar owner)
        {
            _owner = owner;
            AutoScroll = true;
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Your groups";
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw | ControlStyles.Selectable,
                true);
        }

        public IReadOnlyList<SidebarItem> Items => _items;

        public int ContentHeight => _items.Count == 0 ? 0 : LogicalToDeviceUnits(((_items.Count - 1) * ItemPitch) + ItemHeight);

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                _selectedIndex = value >= 0 && value < _items.Count ? value : -1;
                if (_selectedIndex >= 0)
                {
                    _focusIndex = _selectedIndex;
                }

                Invalidate();
            }
        }

        public void SetItems(IReadOnlyList<SidebarItem> items, int selectedIndex)
        {
            _items = items.ToList();
            _hoverIndex = -1;
            AutoScrollMinSize = new Size(0, ContentHeight);
            SelectedIndex = selectedIndex;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            // Enter must open the row instead of triggering the form's AcceptButton.
            return keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_items.Count == 0)
            {
                return;
            }

            var next = e.KeyCode switch
            {
                Keys.Up => Math.Max(0, _focusIndex - 1),
                Keys.Down => Math.Min(_items.Count - 1, _focusIndex + 1),
                Keys.Home => 0,
                Keys.End => _items.Count - 1,
                _ => -1,
            };

            if (next >= 0)
            {
                _focusIndex = next;
                EnsureVisible(next);
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                _owner.GroupClicked?.Invoke(_owner, _focusIndex);
                e.Handled = true;
            }
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var index = IndexAt(e.Y);
            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverIndex = -1;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Focus();
            if (e.Button == MouseButtons.Left && IndexAt(e.Y) is var index and >= 0)
            {
                _focusIndex = index;
                _owner.GroupClicked?.Invoke(_owner, index);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Continue the sidebar gradient behind the rows.
            if (_owner._tokens is not { } t)
            {
                e.Graphics.Clear(SystemColors.Control);
                return;
            }

            var stops = t.SidebarGradient;
            var ownerRect = new Rectangle(0, -Top, Width, Math.Max(1, _owner.Height));
            using var brush = new LinearGradientBrush(ownerRect, stops[0], stops[^1], LinearGradientMode.Vertical);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_owner._tokens is not { } t)
            {
                return;
            }

            var g = e.Graphics;
            for (var i = 0; i < _items.Count; i++)
            {
                var bounds = ItemBounds(i);
                if (!bounds.IntersectsWith(e.ClipRectangle))
                {
                    continue;
                }

                var selected = i == _selectedIndex;
                if (selected || i == _hoverIndex)
                {
                    var fill = selected ? t.SidebarSelected : Draw.Blend(t.Sidebar, t.SidebarSelected, 0.5);
                    Draw.RoundedBox(g, bounds, LogicalToDeviceUnits(10), fill);
                }

                if (selected)
                {
                    // 3px accent bar on the left edge of the selected pill.
                    Draw.RoundedBox(g, new Rectangle(bounds.X, bounds.Y + LogicalToDeviceUnits(14), LogicalToDeviceUnits(3), bounds.Height - LogicalToDeviceUnits(28)), 1.5f, t.AccentSoft);
                }

                if (Focused && i == _focusIndex && ShowFocusCues)
                {
                    Draw.RoundedBox(g, Rectangle.Inflate(bounds, -1, -1), LogicalToDeviceUnits(10), Color.Transparent, t.AccentSoft, 2f);
                }

                var icon = LogicalToDeviceUnits(20);
                if (_items[i].Image is { } image)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(image, new Rectangle(bounds.X + LogicalToDeviceUnits(14), bounds.Y + ((bounds.Height - icon) / 2), icon, icon));
                }

                var textLeft = bounds.X + LogicalToDeviceUnits(50);
                TextRenderer.DrawText(
                    g,
                    _items[i].Name,
                    selected ? EditorFonts.BodyStrong : EditorFonts.Body,
                    new Rectangle(textLeft, bounds.Y, bounds.Right - textLeft - LogicalToDeviceUnits(8), bounds.Height),
                    t.NavText,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        private void EnsureVisible(int index)
        {
            var bounds = ItemBounds(index);
            var offset = -AutoScrollPosition.Y;
            if (bounds.Top < 0)
            {
                AutoScrollPosition = new Point(0, offset + bounds.Top);
            }
            else if (bounds.Bottom > ClientSize.Height)
            {
                AutoScrollPosition = new Point(0, offset + bounds.Bottom - ClientSize.Height);
            }
        }

        private Rectangle ItemBounds(int index)
        {
            var y = AutoScrollPosition.Y + LogicalToDeviceUnits(index * ItemPitch);
            var side = LogicalToDeviceUnits(16);
            return new Rectangle(side, y, ClientSize.Width - (2 * side), LogicalToDeviceUnits(ItemHeight));
        }

        private int IndexAt(int y)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (ItemBounds(i).Contains(LogicalToDeviceUnits(24), y))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
