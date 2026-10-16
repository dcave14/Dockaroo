using System.Drawing;
using System.Windows.Forms;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Controls;

/// <summary>
/// A design-spec card: rounded 14px surface with a 1px border on the canvas, and an optional
/// section title at (20, 20). Children are positioned by the owner's layout code; the card's
/// BackColor is the surface, so child controls blend into it.
/// </summary>
internal sealed class CardPanel : Panel, IThemedControl
{
    public const int Radius = 14;
    public const int Padding20 = 20;

    private Color _canvas = SystemColors.Control;
    private Color _border = SystemColors.ControlDark;
    private Color _text = SystemColors.ControlText;

    public CardPanel(string? title = null)
    {
        Title = title;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Section heading drawn at the top-left; null for none.</summary>
    public string? Title { get; set; }

    /// <summary>Height of the title band (title baseline area) in device pixels.</summary>
    public int TitleBandHeight => LogicalToDeviceUnits(52);

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _canvas = tokens.Canvas;
        _border = tokens.Border;
        _text = tokens.Text;
        BackColor = tokens.Surface;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The corners outside the rounded rectangle show the canvas behind the card.
        e.Graphics.Clear(_canvas);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.RoundedBox(e.Graphics, ClientRectangle, LogicalToDeviceUnits(Radius), BackColor, _border);
        if (!string.IsNullOrEmpty(Title))
        {
            var pad = LogicalToDeviceUnits(Padding20);
            TextRenderer.DrawText(
                e.Graphics,
                Title,
                EditorFonts.Section,
                new Rectangle(pad, pad - LogicalToDeviceUnits(2), Width - (2 * pad), LogicalToDeviceUnits(28)),
                _text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        base.OnPaint(e);
    }
}
