using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Controls;

/// <summary>Base for the owner-drawn, focusable value controls below.</summary>
internal abstract class ThemedValueControl : Control, IThemedControl
{
    protected ThemedValueControl()
    {
        SetStyle(
            ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick,
            true);
        TabStop = true;
    }

    protected ThemeTokens? Tokens { get; private set; }

    public virtual void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        Tokens = tokens;
        Font = EditorFonts.Body;
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

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    protected bool ShowFocus => Focused && ShowFocusCues;
}

/// <summary>
/// "−  4  +" stepper (Apps per row): 136x40, value centred between two 40px buttons. Arrow keys,
/// +/- and Home/End change the value too.
/// </summary>
internal sealed class Stepper : ThemedValueControl
{
    private int _value;
    private int _hoverPart;

    public int Minimum { get; set; } = 1;

    public int Maximum { get; set; } = 20;

    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Up or Keys.Right or Keys.Add or Keys.Oemplus:
                Value++;
                e.Handled = true;
                break;
            case Keys.Down or Keys.Left or Keys.Subtract or Keys.OemMinus:
                Value--;
                e.Handled = true;
                break;
            case Keys.Home:
                Value = Minimum;
                e.Handled = true;
                break;
            case Keys.End:
                Value = Maximum;
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var part = PartAt(e.X);
        if (part != _hoverPart)
        {
            _hoverPart = part;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPart = 0;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Value += PartAt(e.X);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Tokens is not { } t)
        {
            return;
        }

        var g = e.Graphics;
        var cell = LogicalToDeviceUnits(40);
        Draw.RoundedBox(g, ClientRectangle, LogicalToDeviceUnits(ThemedButton.Radius), t.Field, ShowFocus ? t.Primary : t.Border, ShowFocus ? 2f : 1f);
        using (var pen = new Pen(t.Border))
        {
            g.DrawLine(pen, cell, 1, cell, Height - 2);
            g.DrawLine(pen, Width - cell - 1, 1, Width - cell - 1, Height - 2);
        }

        DrawPart(g, new Rectangle(0, 0, cell, Height), Glyphs.Minus, Value > Minimum && Enabled, _hoverPart == -1, t);
        DrawPart(g, new Rectangle(Width - cell, 0, cell, Height), Glyphs.Add, Value < Maximum && Enabled, _hoverPart == 1, t);
        TextRenderer.DrawText(
            g,
            Value.ToString(),
            EditorFonts.Body,
            new Rectangle(cell, 0, Width - (2 * cell), Height),
            Enabled ? t.Text : t.Faint,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static void DrawPart(Graphics g, Rectangle bounds, string glyph, bool enabled, bool hover, ThemeTokens t)
    {
        if (hover && enabled)
        {
            using var brush = new SolidBrush(t.Selected);
            g.FillRectangle(brush, Rectangle.Inflate(bounds, -3, -3));
        }

        TextRenderer.DrawText(g, glyph, EditorFonts.IconSmall, bounds, enabled ? t.Text : t.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private int PartAt(int x)
    {
        var cell = LogicalToDeviceUnits(40);
        return x < cell ? -1 : x >= Width - cell ? 1 : 0;
    }
}

/// <summary>One segment of a <see cref="SegmentedControl"/>.</summary>
internal sealed record Segment(string Text, string? Glyph);

/// <summary>
/// Equal-width segmented choice (Popup background: Dark / Light / Custom). The selected segment
/// gets the selected fill, border and accent text; a segment may show a colour swatch (Custom).
/// Left/Right move the selection; clicking an already selected segment raises
/// <see cref="SegmentClicked"/> so Custom can reopen its colour picker.
/// </summary>
internal sealed class SegmentedControl : ThemedValueControl
{
    private readonly List<Segment> _segments = new();
    private int _selectedIndex = -1;
    private int _hoverIndex = -1;

    public IList<Segment> Segments => _segments;

    /// <summary>Optional swatch colour drawn in the segment at <see cref="SwatchIndex"/>.</summary>
    public Color? Swatch { get; set; }

    public int SwatchIndex { get; set; } = -1;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value == _selectedIndex || value < -1 || value >= _segments.Count)
            {
                return;
            }

            _selectedIndex = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedIndexChanged;

    /// <summary>Raised for every mouse/keyboard activation of a segment, with its index.</summary>
    public event EventHandler<int>? SegmentClicked;

    protected override bool IsInputKey(Keys keyData)
    {
        // Enter must reach OnKeyDown instead of the form's AcceptButton.
        return keyData is Keys.Left or Keys.Right or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Arrows only move the selection; Space/Enter activate (Custom opens its colour picker).
        if (e.KeyCode == Keys.Left && _selectedIndex > 0)
        {
            SelectedIndex = _selectedIndex - 1;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Right && _selectedIndex < _segments.Count - 1)
        {
            SelectedIndex = _selectedIndex + 1;
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Space or Keys.Enter && _selectedIndex >= 0)
        {
            Activate(_selectedIndex);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.X);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
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
        if (e.Button == MouseButtons.Left && Enabled && IndexAt(e.X) is var index and >= 0)
        {
            Activate(index);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Tokens is not { } t || _segments.Count == 0)
        {
            return;
        }

        var g = e.Graphics;
        var radius = LogicalToDeviceUnits(ThemedButton.Radius);
        Draw.RoundedBox(g, ClientRectangle, radius, t.Field, ShowFocus ? t.Primary : t.Border, ShowFocus ? 2f : 1f);
        for (var i = 0; i < _segments.Count; i++)
        {
            var bounds = SegmentBounds(i);
            if (i == _selectedIndex)
            {
                Draw.RoundedBox(g, Rectangle.Inflate(bounds, -2, -2), radius - 2, t.Selected, t.SelectedBorder);
            }
            else if (i == _hoverIndex && Enabled)
            {
                Draw.RoundedBox(g, Rectangle.Inflate(bounds, -2, -2), radius - 2, Draw.Blend(t.Field, t.Text, 0.05));
            }
            else if (i > 0 && i - 1 != _selectedIndex)
            {
                using var pen = new Pen(t.Border);
                g.DrawLine(pen, bounds.Left, LogicalToDeviceUnits(8), bounds.Left, Height - LogicalToDeviceUnits(8));
            }

            DrawSegmentContent(g, i, bounds, i == _selectedIndex ? t.Primary : Enabled ? t.Text : t.Faint, t);
        }
    }

    private void DrawSegmentContent(Graphics g, int index, Rectangle bounds, Color color, ThemeTokens t)
    {
        var segment = _segments[index];
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var gap = LogicalToDeviceUnits(8);
        var glyphWidth = segment.Glyph is null ? 0 : TextRenderer.MeasureText(g, segment.Glyph, EditorFonts.Icon, Size.Empty, flags).Width;
        var textWidth = TextRenderer.MeasureText(g, segment.Text, EditorFonts.Body, Size.Empty, flags).Width;
        // The swatch only makes sense while its segment (Custom) is the active choice.
        var swatchSize = index == SwatchIndex && index == _selectedIndex && Swatch is not null ? LogicalToDeviceUnits(14) : 0;
        var total = glyphWidth + (glyphWidth > 0 ? gap : 0) + textWidth + (swatchSize > 0 ? gap + swatchSize : 0);
        var x = bounds.X + Math.Max(LogicalToDeviceUnits(6), (bounds.Width - total) / 2);
        if (segment.Glyph is not null)
        {
            TextRenderer.DrawText(g, segment.Glyph, EditorFonts.Icon, new Rectangle(x, bounds.Y, glyphWidth, bounds.Height), color, flags);
            x += glyphWidth + gap;
        }

        TextRenderer.DrawText(g, segment.Text, EditorFonts.Body, new Rectangle(x, bounds.Y, textWidth, bounds.Height), color, flags);
        x += textWidth + gap;
        if (swatchSize > 0)
        {
            var swatch = new Rectangle(x, bounds.Y + ((bounds.Height - swatchSize) / 2), swatchSize, swatchSize);
            Draw.RoundedBox(g, swatch, LogicalToDeviceUnits(3), Swatch!.Value, t.Border);
        }
    }

    private void Activate(int index)
    {
        SelectedIndex = index;
        SegmentClicked?.Invoke(this, index);
    }

    private Rectangle SegmentBounds(int index)
    {
        var width = Width / _segments.Count;
        var x = index * width;
        return new Rectangle(x, 0, index == _segments.Count - 1 ? Width - x : width, Height);
    }

    private int IndexAt(int x)
    {
        if (_segments.Count == 0 || x < 0 || x >= Width)
        {
            return -1;
        }

        return Math.Min(_segments.Count - 1, x / Math.Max(1, Width / _segments.Count));
    }
}

/// <summary>44x26 on/off switch (Allow open all). A real CheckBox: Space toggles it.</summary>
internal sealed class ToggleSwitch : CheckBox, IThemedControl
{
    private ThemeTokens? _tokens;

    public ToggleSwitch()
    {
        Appearance = System.Windows.Forms.Appearance.Button;
        FlatStyle = FlatStyle.Flat;
        Text = string.Empty;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        Invalidate();
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
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

        var track = new Rectangle(0, 0, Width, Height);
        var radius = Height / 2f;
        var on = Checked;
        Draw.RoundedBox(g, track, radius, on ? t.Primary : t.Slider, on ? null : t.Border);
        if (Focused && ShowFocusCues)
        {
            Draw.RoundedBox(g, Rectangle.Inflate(track, -1, -1), radius, Color.Transparent, t.Text, 2f);
        }

        var inset = LogicalToDeviceUnits(4);
        var knob = Height - (2 * inset);
        var x = on ? Width - inset - knob : inset;
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(on ? t.PrimaryForeground : t.Muted))
        {
            g.FillEllipse(brush, x, inset, knob, knob);
        }

        g.SmoothingMode = mode;
    }
}

/// <summary>
/// Transparency slider, 10..90 in steps of 10 (design spec): a 4px track filled up to the value
/// and a 16px thumb. Mouse drag snaps to steps; arrow keys, PageUp/PageDown, Home/End work.
/// </summary>
internal sealed class TransparencySlider : ThemedValueControl
{
    private int _value = 10;
    private bool _dragging;

    public int Minimum { get; set; } = 10;

    public int Maximum { get; set; } = 90;

    public int Step { get; set; } = 10;

    public int Value
    {
        get => _value;
        set
        {
            var snapped = Math.Clamp((int)Math.Round(value / (double)Step) * Step, Minimum, Maximum);
            if (snapped == _value)
            {
                return;
            }

            _value = snapped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down or Keys.PageDown:
                Value -= Step;
                e.Handled = true;
                break;
            case Keys.Right or Keys.Up or Keys.PageUp:
                Value += Step;
                e.Handled = true;
                break;
            case Keys.Home:
                Value = Minimum;
                e.Handled = true;
                break;
            case Keys.End:
                Value = Maximum;
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            _dragging = true;
            Capture = true;
            Value = ValueAt(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Value = ValueAt(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    // A dialog or Alt+Tab can take the capture mid-drag; stop following the mouse then.
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            _dragging = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Tokens is not { } t)
        {
            return;
        }

        var g = e.Graphics;
        var thumb = LogicalToDeviceUnits(16);
        var trackHeight = LogicalToDeviceUnits(4);
        var left = thumb / 2;
        var right = Width - (thumb / 2);
        var y = (Height - trackHeight) / 2;
        var thumbX = XFor(_value);
        Draw.RoundedBox(g, new Rectangle(left, y, right - left, trackHeight), trackHeight / 2f, t.Slider);
        Draw.RoundedBox(g, new Rectangle(left, y, Math.Max(trackHeight, thumbX - left), trackHeight), trackHeight / 2f, Enabled ? t.Primary : t.Faint);

        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var thumbRect = new Rectangle(thumbX - (thumb / 2), (Height - thumb) / 2, thumb - 1, thumb - 1);
        using (var fill = new SolidBrush(Enabled ? t.Primary : t.Faint))
        {
            g.FillEllipse(fill, thumbRect);
        }

        if (ShowFocus)
        {
            using var ring = new Pen(t.Text, 2f);
            g.DrawEllipse(ring, Rectangle.Inflate(thumbRect, 2, 2));
        }

        g.SmoothingMode = mode;
    }

    private int XFor(int value)
    {
        var thumb = LogicalToDeviceUnits(16);
        var span = Width - thumb;
        return (thumb / 2) + (int)Math.Round(span * (value - Minimum) / (double)Math.Max(1, Maximum - Minimum));
    }

    private int ValueAt(int x)
    {
        var thumb = LogicalToDeviceUnits(16);
        var span = Math.Max(1, Width - thumb);
        var fraction = Math.Clamp((x - (thumb / 2)) / (double)span, 0, 1);
        return Minimum + (int)Math.Round(fraction * (Maximum - Minimum));
    }
}
