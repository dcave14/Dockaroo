using System.Drawing;
using System.Windows.Forms;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Controls;

/// <summary>Visual role of a <see cref="ThemedButton"/>.</summary>
internal enum ButtonKind
{
    /// <summary>Filled accent (Save changes, Add shortcut).</summary>
    Primary,

    /// <summary>Outlined neutral (Browse, Create taskbar shortcut).</summary>
    Secondary,

    /// <summary>Outlined with danger text (Delete group).</summary>
    Danger,

    /// <summary>Outlined on the sidebar (New group).</summary>
    Nav,

    /// <summary>Borderless icon button (row overflow menu).</summary>
    Ghost,
}

/// <summary>
/// An owner-drawn button in the design-spec style (8px radius, optional leading glyph, 2px
/// accent focus ring). Still a real <see cref="Button"/>: keyboard activation, AcceptButton and
/// accessibility keep working. Corners show the parent's background colour.
/// </summary>
internal sealed class ThemedButton : Button, IThemedControl
{
    public const int Radius = 8;

    private ThemeTokens? _tokens;
    private bool _hover;
    private bool _pressed;

    public ThemedButton(string text, ButtonKind kind, string? glyph = null)
    {
        Text = text;
        Kind = kind;
        Glyph = glyph;
        FlatStyle = FlatStyle.Flat;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public ButtonKind Kind { get; set; }

    /// <summary>Optional icon-font glyph drawn before the text (or alone when Text is empty).</summary>
    public string? Glyph { get; set; }

    /// <summary>Selected option (mode choice, active navigation item): selected fill and accent.</summary>
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

    private bool _selected;

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        Font = Kind == ButtonKind.Primary ? EditorFonts.BodyStrong : EditorFonts.Body;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = mevent.Button == MouseButtons.Left;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(Parent?.BackColor ?? SystemColors.Control);
        if (_tokens is not { } t)
        {
            return;
        }

        var (fill, border, text) = ColorsFor(t);
        Draw.RoundedBox(g, ClientRectangle, LogicalToDeviceUnits(Radius), fill, border);

        if (Focused && ShowFocusCues)
        {
            var ring = Rectangle.Inflate(ClientRectangle, -1, -1);
            Draw.RoundedBox(g, ring, LogicalToDeviceUnits(Radius), Color.Transparent, t.Primary, 2f);
        }

        var content = Rectangle.Inflate(ClientRectangle, -LogicalToDeviceUnits(12), 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        if (string.IsNullOrEmpty(Glyph))
        {
            TextRenderer.DrawText(g, Text, Font, content, text, flags | TextFormatFlags.HorizontalCenter);
            return;
        }

        var glyphFont = EditorFonts.Icon;
        var glyphSize = TextRenderer.MeasureText(g, Glyph, glyphFont, Size.Empty, TextFormatFlags.NoPadding);
        if (string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(g, Glyph, glyphFont, ClientRectangle, text, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            return;
        }

        var gap = LogicalToDeviceUnits(8);
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var total = Math.Min(content.Width, glyphSize.Width + gap + textSize.Width);
        var x = content.X + ((content.Width - total) / 2);
        TextRenderer.DrawText(g, Glyph, glyphFont, new Rectangle(x, 0, glyphSize.Width, Height), text, flags | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphSize.Width + gap, 0, Math.Max(0, content.Right - x - glyphSize.Width - gap), Height), text, flags | TextFormatFlags.NoPadding);
    }

    private (Color Fill, Color? Border, Color Text) ColorsFor(ThemeTokens t)
    {
        var surface = Parent?.BackColor ?? t.Surface;
        if (!Enabled)
        {
            return Kind == ButtonKind.Primary
                ? (t.Border, null, t.Faint)
                : (surface, t.Border, t.Faint);
        }

        if (_selected)
        {
            return Kind == ButtonKind.Nav
                ? (t.SidebarSelected, t.SidebarSelected, t.NavText)
                : (t.Selected, t.SelectedBorder, t.Primary);
        }

        return Kind switch
        {
            ButtonKind.Primary => (_pressed ? Draw.Blend(t.PrimaryHover, t.Text, 0.1) : _hover ? t.PrimaryHover : t.Primary, null, t.PrimaryForeground),
            ButtonKind.Danger => (_hover ? Draw.Blend(surface, t.Danger, 0.12) : surface, t.Border, t.Danger),
            ButtonKind.Nav => (_hover ? Draw.Blend(surface, t.NavText, 0.08) : surface, t.NavLine, t.NavText),
            ButtonKind.Ghost => (_hover ? Draw.Blend(surface, t.Text, 0.08) : surface, null, t.Muted),
            _ => (_hover ? Draw.Blend(surface, t.Text, 0.06) : surface, t.Border, t.Text),
        };
    }
}
