using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>
/// Paints a group popup: the surface (the Dark theme's taskbar replica, or the group's own
/// colour), the hover plate and the icon plates. Shared by <see cref="PopupForm"/> and the
/// editor's taskbar preview, so the preview shows exactly what the popup draws. Memory only:
/// icons come from the caller's lookup, never from disk. The group's transparency applies to
/// the surface alone: icons and the hover plate are always drawn opaque, so the target must
/// keep per-pixel alpha (a 32bpp bitmap) for the surface to show through.
/// </summary>
internal sealed class PopupPainter : IDisposable
{
    private const int LightThreshold = 255 / 2;

    private static readonly Color FallbackBackground = Color.FromArgb(31, 31, 31);
    private static readonly SolidBrush TaskbarFillBrush = new(TaskbarPalette.Fill);
    private static readonly SolidBrush TaskbarTrimBrush = new(TaskbarPalette.Trim);
    private static readonly SolidBrush TaskbarPlateBrush = new(TaskbarPalette.Plate);
    private static readonly SolidBrush TaskbarPlateTopEdgeBrush = new(TaskbarPalette.PlateTopEdge);


    private readonly SolidBrush _background;
    private readonly SolidBrush _taskbarFill;
    private readonly SolidBrush _taskbarTrim;
    private readonly SolidBrush _hoverBrush;
    private readonly SolidBrush _placeholderBrush = new(Color.FromArgb(48, 255, 255, 255));
    private readonly SolidBrush _letterBrush;

    // Per painter, not static: GDI+ objects must not be used from two threads at once.
    private readonly StringFormat _centeredText = new(StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        HotkeyPrefix = HotkeyPrefix.None,
    };

    /// <summary>
    /// <paramref name="backgroundOpacity"/> (0..1, see <see cref="OpacityFor"/>) is the surface's
    /// alpha; 1 paints a solid popup.
    /// </summary>
    public PopupPainter(string? colorHex, double backgroundOpacity = 1)
    {
        // Legacy look: the background colour comes from the group (frmMain ctor). The Dark
        // theme (the measured default) gets the taskbar-matched surface instead.
        TaskbarLook = string.Equals(colorHex, Group.DarkColorHex, StringComparison.OrdinalIgnoreCase);
        BackColor = ParseColor(colorHex);
        TextColor = IsLight(BackColor) ? Color.Black : Color.WhiteSmoke;
        BackgroundAlpha = (int)Math.Round(Math.Clamp(backgroundOpacity, 0, 1) * 255);
        _background = new SolidBrush(Color.FromArgb(BackgroundAlpha, BackColor));
        _taskbarFill = new SolidBrush(Color.FromArgb(BackgroundAlpha, TaskbarPalette.Fill));
        _taskbarTrim = new SolidBrush(Color.FromArgb(BackgroundAlpha, TaskbarPalette.Trim));
        _hoverBrush = new SolidBrush(ComputeHoverColor(BackColor));
        _letterBrush = new SolidBrush(TextColor);
    }

    /// <summary>Alpha (0..255) the surface is painted with; icons are always 255.</summary>
    public int BackgroundAlpha { get; }

    /// <summary>The group's popup colour (the form's BackColor).</summary>
    public Color BackColor { get; }

    /// <summary>True for the Dark theme, which is drawn as a piece of the taskbar.</summary>
    public bool TaskbarLook { get; }

    /// <summary>Placeholder letter colour on this background.</summary>
    public Color TextColor { get; }

    /// <summary>
    /// Surface opacity for a stored transparency percentage. Rendering never goes past
    /// <see cref="Group.MaxRenderedTransparency"/>, so a stored 100 (legacy/imported) still shows a
    /// visible popup instead of an invisible window that takes clicks.
    /// </summary>
    public static double OpacityFor(int transparency)
    {
        return 1 - (Math.Clamp(transparency, 0, Group.MaxRenderedTransparency) / 100.0);
    }

    /// <summary>
    /// Paints the whole popup into <paramref name="graphics"/> at (0,0). <paramref name="iconFor"/>
    /// returns the in-memory icon for an entry (null draws the placeholder letter).
    /// </summary>
    public void Paint(
        Graphics graphics,
        Size size,
        PopupMetrics metrics,
        int count,
        int perRow,
        int hoveredIndex,
        Func<int, Bitmap?> iconFor,
        Func<int, string> nameFor,
        Font font)
    {
        if (TaskbarLook)
        {
            PaintTaskbarBackground(graphics, size, _taskbarFill, _taskbarTrim);
        }
        else
        {
            graphics.FillRectangle(_background, 0, 0, size.Width, size.Height);
        }

        for (var i = 0; i < count; i++)
        {
            var cell = PopupGeometry.TileBounds(i, perRow, metrics);
            if (i == hoveredIndex)
            {
                DrawHoverPlate(graphics, cell.X, cell.Y, metrics);
            }

            var iconMargin = (metrics.PlateSize - metrics.IconSize) / 2;
            var bounds = new Rectangle(cell.X + iconMargin, cell.Y + iconMargin, metrics.IconSize, metrics.IconSize);
            if (iconFor(i) is { } bitmap)
            {
                // Source image files keep their own dimensions; draw the whole bitmap into the
                // box so the art is always fitted and contained in the icon area.
                graphics.DrawImage(bitmap, bounds, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
                continue;
            }

            // Missing icon: a plain placeholder. Extraction is never triggered here.
            graphics.FillRectangle(_placeholderBrush, bounds);
            var name = nameFor(i);
            var letter = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
            // GDI+ text, not TextRenderer: GDI writes alpha 0 into a 32bpp target, which would
            // punch the letter out of a translucent popup.
            var hint = graphics.TextRenderingHint;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(letter, font, _letterBrush, bounds, _centeredText);
            graphics.TextRenderingHint = hint;
        }
    }

    /// <summary>
    /// Paints the Dark-theme taskbar surface: the measured flat fill plus the 1px light-catch
    /// trim around the window's outer edge, so the popup reads as a piece of the taskbar.
    /// </summary>
    public static void PaintTaskbarBackground(Graphics graphics, Size size) =>
        PaintTaskbarBackground(graphics, size, TaskbarFillBrush, TaskbarTrimBrush);

    private static void PaintTaskbarBackground(Graphics graphics, Size size, Brush fill, Brush trim)
    {
        graphics.FillRectangle(fill, 0, 0, size.Width, size.Height);

        // Pixel-exact 1px trim on all four outer edges; a 1px pen would straddle the boundary.
        // SourceCopy: a translucent trim replaces the fill under it instead of stacking on it.
        var mode = graphics.CompositingMode;
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.FillRectangle(trim, 0, 0, size.Width, 1);
        graphics.FillRectangle(trim, 0, size.Height - 1, size.Width, 1);
        graphics.FillRectangle(trim, 0, 1, 1, size.Height - 2);
        graphics.FillRectangle(trim, size.Width - 1, 1, 1, size.Height - 2);
        graphics.CompositingMode = mode;
    }

    public void Dispose()
    {
        _background.Dispose();
        _taskbarFill.Dispose();
        _taskbarTrim.Dispose();
        _hoverBrush.Dispose();
        _placeholderBrush.Dispose();
        _letterBrush.Dispose();
        _centeredText.Dispose();
    }

    private void DrawHoverPlate(Graphics graphics, int x, int y, PopupMetrics metrics)
    {
        if (TaskbarLook)
        {
            FillRoundedPlate(graphics, TaskbarPlateBrush, x, y, metrics);
            graphics.FillRectangle(TaskbarPlateTopEdgeBrush, x + metrics.PlateRadius, y, metrics.PlateSize - (2 * metrics.PlateRadius), 1);
            return;
        }

        // Light and Custom keep their own hover colour; the taskbar-scale plate shape is shared.
        FillRoundedPlate(graphics, _hoverBrush, x, y, metrics);
    }

    /// <summary>Fills a plate-sized rounded rectangle without allocating a path per paint.</summary>
    private static void FillRoundedPlate(Graphics graphics, Brush brush, int x, int y, PopupMetrics metrics)
    {
        var size = metrics.PlateSize;
        var radius = metrics.PlateRadius;
        var corner = radius * 2;
        graphics.FillRectangle(brush, x + radius, y, size - corner, size);
        graphics.FillRectangle(brush, x, y + radius, size, size - corner);
        graphics.FillEllipse(brush, x, y, corner, corner);
        graphics.FillEllipse(brush, x + size - corner, y, corner, corner);
        graphics.FillEllipse(brush, x, y + size - corner, corner, corner);
        graphics.FillEllipse(brush, x + size - corner, y + size - corner, corner, corner);
    }

    private static Color ParseColor(string? colorHex)
    {
        return Group.IsValidColorHex(colorHex) ? ColorTranslator.FromHtml(colorHex!) : FallbackBackground;
    }

    private static bool IsLight(Color color)
    {
        return (color.R * 0.2126) + (color.G * 0.7152) + (color.B * 0.0722) > LightThreshold;
    }

    private static Color ComputeHoverColor(Color backColor)
    {
        // Legacy frmMain ctor: light backgrounds hover darker, dark backgrounds hover lighter.
        // Values are clamped here; the legacy code could throw on saturated colors.
        if (IsLight(backColor))
        {
            return Color.FromArgb(
                backColor.A,
                Math.Max(backColor.R - 50, 0),
                Math.Max(backColor.G - 50, 0),
                Math.Max(backColor.B - 50, 0));
        }

        return Color.FromArgb(
            backColor.A,
            Math.Min(backColor.R + 50, 255),
            Math.Min(backColor.G + 50, 255),
            Math.Min(backColor.B + 50, 255));
    }
}
