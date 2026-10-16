using System.Drawing;
using System.Windows.Forms;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Controls;

/// <summary>
/// A 40px rounded input (8px radius) hosting a borderless <see cref="TextBox"/>. The border
/// turns into the 2px accent while the box has focus and into the danger colour while
/// <see cref="HasError"/> is set. Callers work with <see cref="Inner"/> directly.
/// </summary>
internal sealed class ThemedField : UserControl, IThemedControl
{
    private ThemeTokens? _tokens;
    private bool _hasError;

    public ThemedField(TextBox inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Inner.BorderStyle = BorderStyle.None;
        Inner.Font = EditorFonts.Body;
        Inner.Enter += (_, _) => Invalidate();
        Inner.Leave += (_, _) => Invalidate();
        Controls.Add(Inner);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.IBeam;
    }

    public TextBox Inner { get; }

    /// <summary>Paints the danger border (validation errors).</summary>
    public bool HasError
    {
        get => _hasError;
        set
        {
            _hasError = value;
            Invalidate();
        }
    }

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        UpdateInnerColors();
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Inner.Focus();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        UpdateInnerColors();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var pad = LogicalToDeviceUnits(12);
        var height = Inner.PreferredHeight;
        Inner.Bounds = new Rectangle(pad, Math.Max(0, (Height - height) / 2), Math.Max(0, Width - (2 * pad)), height);
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

        var border = _hasError ? t.Danger : Inner.Focused ? t.Primary : t.Border;
        var width = _hasError || Inner.Focused ? 2f : 1f;
        Draw.RoundedBox(e.Graphics, ClientRectangle, LogicalToDeviceUnits(ThemedButton.Radius), FieldFill(t), border, width);
    }

    private Color FieldFill(ThemeTokens t) => Enabled ? t.Field : Draw.Blend(t.Field, t.Canvas, 0.6);

    private void UpdateInnerColors()
    {
        if (_tokens is not { } t)
        {
            return;
        }

        Inner.BackColor = FieldFill(t);
        Inner.ForeColor = Enabled ? t.Text : t.Faint;
    }
}
