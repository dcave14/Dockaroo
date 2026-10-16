using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor;

/// <summary>
/// The editor's Appearance page (design theme-variations/implementation-spec.md): app mode
/// (Light / Dark / System) and the five matched logo palettes, applied immediately and saved
/// automatically, plus a "Your current look" sample. Global app settings, independent of every
/// group's popup appearance. Selection is shown by border, fill and a check mark, not colour alone.
/// </summary>
internal sealed class AppearancePage : Panel, IThemedControl
{
    private const int Gutter = 32;
    private const int TileWidth = 192;
    private const int TileHeight = 148;
    private const int TileGap = 16;

    private readonly IAppearanceSettings _settings;
    private readonly IEditorThemeSource _theme;
    private readonly Label _title = new() { Text = "Appearance", AutoEllipsis = true };
    private readonly Label _subtitle = new() { Text = "Make the app feel like yours." };
    private readonly CardPanel _modeCard = new("App mode");
    private readonly Label _modeHint = new() { Text = "Choose a mode, or follow your Windows setting." };
    private readonly ThemedButton _light = new("Light", ButtonKind.Secondary, Glyphs.Sun);
    private readonly ThemedButton _dark = new("Dark", ButtonKind.Secondary, Glyphs.Moon);
    private readonly ThemedButton _system = new("System", ButtonKind.Secondary, Glyphs.System);
    private readonly CardPanel _paletteCard = new("App color & logo");
    private readonly Label _paletteHint = new() { Text = "One choice updates the app logo, accents, and surfaces together." };
    private readonly List<PaletteTile> _tiles = new();
    private readonly CardPanel _lookCard = new("Your current look");
    private readonly Label _lookSummary = new();
    private readonly LookSample _sample;
    private readonly Label _note1 = new() { Text = "Applies to the app interface and logo." };
    private readonly Label _note2 = new() { Text = "Group popup backgrounds stay independent." };
    private readonly Label _note3 = new() { Text = "Changes are saved automatically." };
    private ThemeTokens? _tokens;
    private ResolvedMode _mode;

    public AppearancePage(IAppearanceSettings settings, IEditorThemeSource theme)
    {
        _settings = settings;
        _theme = theme;
        AutoScroll = true;
        _sample = new LookSample(this);

        _title.Font = EditorFonts.Title;
        _subtitle.Font = EditorFonts.Body;
        foreach (var label in new[] { _modeHint, _paletteHint, _lookSummary, _note2, _note3 })
        {
            label.Font = EditorFonts.Label;
        }

        _note1.Font = EditorFonts.Body;
        _lookSummary.Font = EditorFonts.Body;

        _light.Click += (_, _) => Apply(() => _settings.SetMode(AppMode.Light));
        _dark.Click += (_, _) => Apply(() => _settings.SetMode(AppMode.Dark));
        _system.Click += (_, _) => Apply(() => _settings.SetMode(AppMode.System));
        _modeCard.Controls.AddRange(new Control[] { _modeHint, _light, _dark, _system });
        _modeCard.Layout += (_, _) =>
        {
            _modeHint.Bounds = new Rectangle(L(20), L(50), Math.Max(0, _modeCard.Width - L(40)), L(20));
            var x = L(20);
            foreach (var button in new[] { _light, _dark, _system })
            {
                button.Bounds = new Rectangle(x, L(84), L(168), L(44));
                x += L(168) + L(16);
            }
        };

        _paletteCard.Controls.Add(_paletteHint);
        foreach (var palette in ThemeCatalog.Default.Palettes)
        {
            var tile = new PaletteTile(this, palette);
            tile.Click += (_, _) => Apply(() => _settings.SetPalette(tile.Palette.Id));
            _tiles.Add(tile);
            _paletteCard.Controls.Add(tile);
        }

        _paletteCard.Layout += (_, _) => LayoutTiles();

        _lookCard.Controls.AddRange(new Control[] { _lookSummary, _sample, _note1, _note2, _note3 });
        _lookCard.Layout += (_, _) => LayoutLookCard();

        Controls.AddRange(new Control[] { _title, _subtitle, _modeCard, _paletteCard, _lookCard });
        Resize += (_, _) => LayoutPage();
        RefreshSelection();
    }

    /// <summary>Raised with "Appearance saved" or the error text after every change attempt.</summary>
    public event EventHandler<(bool Saved, string Text)>? ChangeAttempted;

    /// <summary>Test hooks.</summary>
    internal IReadOnlyList<Control> PaletteTiles => _tiles;

    internal ThemedButton ModeButton(AppMode mode) => mode switch
    {
        AppMode.Light => _light,
        AppMode.Dark => _dark,
        _ => _system,
    };

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        _mode = mode;
        BackColor = tokens.Canvas;
        NativeTheme.ApplyScrollbars(this, mode);
        foreach (var card in new[] { _modeCard, _paletteCard, _lookCard })
        {
            card.ApplyTheme(tokens, mode);
        }

        foreach (var button in new[] { _light, _dark, _system })
        {
            button.ApplyTheme(tokens, mode);
        }

        _title.ForeColor = tokens.Text;
        _note1.ForeColor = tokens.Text;
        foreach (var label in new[] { _subtitle, _modeHint, _paletteHint, _lookSummary, _note2, _note3 })
        {
            label.ForeColor = tokens.Muted;
        }

        foreach (var label in new[] { _title, _subtitle })
        {
            label.BackColor = tokens.Canvas;
        }

        foreach (var label in new[] { _modeHint, _paletteHint, _lookSummary, _note1, _note2, _note3 })
        {
            label.BackColor = tokens.Surface;
        }

        _sample.ApplyTheme(tokens, mode);
        RefreshSelection();
        Invalidate(true);
    }

    // The page starts hidden, so its window handle can be created after the theme was applied.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeTheme.ApplyScrollbars(this, _mode);
    }

    /// <summary>Re-reads the saved choice into the selection marks and the summary line.</summary>
    public void RefreshSelection()
    {
        var preferences = _settings.Preferences;
        _light.IsSelected = preferences.Mode == AppMode.Light;
        _dark.IsSelected = preferences.Mode == AppMode.Dark;
        _system.IsSelected = preferences.Mode == AppMode.System;
        foreach (var tile in _tiles)
        {
            tile.IsSelected = tile.Palette.Id == preferences.PaletteId;
        }

        var palette = ThemeCatalog.Default.Get(preferences.PaletteId).Name;
        var resolved = _theme.EffectiveMode == ResolvedMode.Dark ? "Dark" : "Light";
        var modeText = preferences.Mode == AppMode.System ? $"System ({resolved.ToLowerInvariant()})" : resolved;
        _lookSummary.Text = $"{palette} {Glyphs.MiddleDot} {modeText} mode";
        _sample.Invalidate();
    }

    private void Apply(Func<string?> change)
    {
        var error = change();
        RefreshSelection();
        ChangeAttempted?.Invoke(this, error is null ? (true, "Appearance saved") : (false, error));
    }

    private void MoveTile(PaletteTile from, int delta)
    {
        var index = _tiles.IndexOf(from) + delta;
        if (index < 0 || index >= _tiles.Count)
        {
            return;
        }

        _tiles[index].Focus();
        _tiles[index].PerformTileClick();
    }

    private void LayoutPage()
    {
        var scroll = AutoScrollPosition;
        var width = Math.Max(L(560), ClientSize.Width - (2 * L(Gutter)));
        var x = L(Gutter);
        _title.Bounds = new Rectangle(x, L(32) + scroll.Y, width, L(48));
        _subtitle.Bounds = new Rectangle(x, L(80) + scroll.Y, width, L(22));
        _modeCard.Bounds = new Rectangle(x, L(128) + scroll.Y, width, L(150));

        var perRow = Math.Max(1, (width - L(40) + L(TileGap)) / (L(TileWidth) + L(TileGap)));
        var rows = (_tiles.Count + perRow - 1) / perRow;
        var paletteHeight = L(86) + (rows * (L(TileHeight) + L(TileGap))) - L(TileGap) + L(24);
        _paletteCard.Bounds = new Rectangle(x, _modeCard.Bottom + L(24), width, paletteHeight);

        var lookHeight = width >= L(900) ? L(228) : L(330);
        _lookCard.Bounds = new Rectangle(x, _paletteCard.Bottom + L(24), width, lookHeight);
        var total = _lookCard.Bottom - scroll.Y + L(Gutter);
        var minSize = new Size(x + width + L(Gutter), total); // horizontal scroll when narrower than the cards
        if (AutoScrollMinSize != minSize)
        {
            AutoScrollMinSize = minSize;
        }
    }

    private void LayoutTiles()
    {
        _paletteHint.Bounds = new Rectangle(L(20), L(50), Math.Max(0, _paletteCard.Width - L(40)), L(20));
        var perRow = Math.Max(1, (_paletteCard.Width - L(40) + L(TileGap)) / (L(TileWidth) + L(TileGap)));
        for (var i = 0; i < _tiles.Count; i++)
        {
            var column = i % perRow;
            var row = i / perRow;
            _tiles[i].Bounds = new Rectangle(
                L(20) + (column * (L(TileWidth) + L(TileGap))),
                L(86) + (row * (L(TileHeight) + L(TileGap))),
                L(TileWidth),
                L(TileHeight));
        }
    }

    private void LayoutLookCard()
    {
        var w = _lookCard.Width;
        _lookSummary.Bounds = new Rectangle(L(20), L(50), Math.Max(0, w - L(40)), L(24));
        _sample.Bounds = new Rectangle(L(20), L(90), Math.Min(L(440), w - L(40)), L(112));
        var notesLeft = w >= L(900) ? L(488) : L(20);
        var notesTop = w >= L(900) ? L(104) : L(214);
        var notesWidth = Math.Max(0, w - notesLeft - L(20));
        _note1.Bounds = new Rectangle(notesLeft, notesTop, notesWidth, L(24));
        _note2.Bounds = new Rectangle(notesLeft, notesTop + L(32), notesWidth, L(20));
        _note3.Bounds = new Rectangle(notesLeft, notesTop + L(62), notesWidth, L(20));
    }

    private int L(int logical) => LogicalToDeviceUnits(logical);

    /// <summary>One palette choice: its logo, name and three swatches; selected = border + check.</summary>
    private sealed class PaletteTile : Control
    {
        private readonly AppearancePage _page;
        private bool _hover;
        private bool _selected;

        public PaletteTile(AppearancePage page, ThemePalette palette)
        {
            _page = page;
            Palette = palette;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.RadioButton;
            AccessibleName = palette.Name;
            SetStyle(
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick,
                true);
        }

        public ThemePalette Palette { get; }

        public void PerformTileClick() => OnClick(EventArgs.Empty);

        public bool IsSelected
        {
            get => _selected;
            set
            {
                _selected = value;
                AccessibleDescription = value ? "Selected" : null;
                Invalidate();
            }
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Enter or Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Space or Keys.Enter)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.KeyCode is Keys.Left or Keys.Up or Keys.Right or Keys.Down)
            {
                // Radio-group behaviour: arrows move to the neighbouring palette and choose it.
                _page.MoveTile(this, e.KeyCode is Keys.Left or Keys.Up ? -1 : 1);
                e.Handled = true;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
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

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_page._tokens is not { } t)
            {
                return;
            }

            var g = e.Graphics;
            var radius = LogicalToDeviceUnits(10);
            var fill = _selected ? t.Selected : _hover ? Draw.Blend(t.Field, t.Text, 0.05) : t.Field;
            Draw.RoundedBox(g, ClientRectangle, radius, fill, _selected ? t.Primary : t.Border, _selected ? 2f : 1f);
            if (Focused && ShowFocusCues)
            {
                Draw.RoundedBox(g, Rectangle.Inflate(ClientRectangle, -4, -4), radius - 2, Color.Transparent, t.Text, 1f);
            }

            var logo = LogicalToDeviceUnits(64);
            g.DrawIcon(_page._settings.GetIcon(Palette.Id, new Size(logo, logo)), new Rectangle((Width - logo) / 2, LogicalToDeviceUnits(16), logo, logo));
            TextRenderer.DrawText(
                g,
                Palette.Name,
                EditorFonts.Body,
                new Rectangle(0, LogicalToDeviceUnits(86), Width, LogicalToDeviceUnits(24)),
                t.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            var dot = LogicalToDeviceUnits(10);
            var gap = LogicalToDeviceUnits(10);
            var total = (Palette.Swatches.Count * dot) + ((Palette.Swatches.Count - 1) * gap);
            var x = (Width - total) / 2;
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var swatch in Palette.Swatches)
            {
                using var brush = new SolidBrush(swatch);
                g.FillEllipse(brush, x, LogicalToDeviceUnits(120), dot, dot);
                x += dot + gap;
            }

            if (_selected)
            {
                // Check badge: the selection never relies on colour alone.
                var badge = LogicalToDeviceUnits(20);
                var badgeRect = new Rectangle(Width - badge - LogicalToDeviceUnits(10), LogicalToDeviceUnits(10), badge, badge);
                using (var brush = new SolidBrush(t.Primary))
                {
                    g.FillEllipse(brush, badgeRect);
                }

                TextRenderer.DrawText(g, Glyphs.CheckMark, EditorFonts.IconSmall, badgeRect, t.PrimaryForeground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            g.SmoothingMode = mode;
        }
    }

    /// <summary>"Your current look": the palette logo, the app name and a primary-action sample.</summary>
    private sealed class LookSample : Control
    {
        private readonly AppearancePage _page;
        private ThemeTokens? _tokens;

        public LookSample(AppearancePage page)
        {
            _page = page;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = "Current look sample";
        }

        public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
        {
            _tokens = tokens;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_tokens is not { } t)
            {
                return;
            }

            var g = e.Graphics;
            Draw.RoundedBox(g, ClientRectangle, LogicalToDeviceUnits(10), t.Field, t.Border);
            var logo = LogicalToDeviceUnits(64);
            var top = (Height - logo) / 2;
            g.DrawIcon(_page._theme.GetAppIcon(new Size(logo, logo)), new Rectangle(LogicalToDeviceUnits(20), top, logo, logo));
            var textLeft = LogicalToDeviceUnits(104);
            TextRenderer.DrawText(
                g,
                AppConstants.AppName,
                EditorFonts.Section,
                new Rectangle(textLeft, top, Width - textLeft - LogicalToDeviceUnits(12), LogicalToDeviceUnits(28)),
                t.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            var button = new Rectangle(textLeft, top + LogicalToDeviceUnits(34), LogicalToDeviceUnits(128), LogicalToDeviceUnits(32));
            Draw.RoundedBox(g, button, LogicalToDeviceUnits(ThemedButton.Radius), t.Primary);
            TextRenderer.DrawText(g, "Primary action", EditorFonts.Label, button, t.PrimaryForeground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
