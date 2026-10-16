using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Dockaroo.App.Appearance;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Popup;

namespace Dockaroo.App.Editor;

/// <summary>What the preview shows: the edit buffer's popup settings and icon lookups.</summary>
internal sealed record PreviewState(
    int Count,
    int Width,
    string ColorHex,
    int Transparency,
    Func<int, Bitmap?> IconFor,
    Func<int, string> NameFor)
{
    public static PreviewState Empty { get; } = new(0, 6, Group.DarkColorHex, 10, _ => null, _ => string.Empty);
}

/// <summary>
/// The "Taskbar preview" scene (design spec): a wallpaper in the palette's colours with a
/// Windows-style taskbar whose group button is selected, and the group's real popup above it,
/// painted by <see cref="PopupPainter"/> from the unsaved edit buffer (colour, transparency,
/// apps per row, icons). The taskbar is an illustration; the app never draws on the real one.
/// </summary>
internal sealed class TaskbarPreview : Control, IThemedControl
{
    private const int TaskbarHeight = 48;
    private const int ScenePadding = 8;

    private ThemeTokens? _tokens;
    private ResolvedMode _mode;
    private PreviewState _state = PreviewState.Empty;

    public TaskbarPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = "Taskbar preview";
    }

    /// <summary>Palette whose logo is drawn as the group's taskbar button (none: no logo).</summary>
    public string? LogoPalette { get; set; }

    /// <summary>Test hook: where the popup was drawn in the last paint, in control pixels.</summary>
    internal Rectangle LastPopupBounds { get; private set; }

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        _mode = mode;
        Invalidate();
    }

    public void SetState(PreviewState state)
    {
        _state = state ?? PreviewState.Empty;
        Invalidate();
    }

    /// <summary>"4 apps \u00B7 1 row" for the card caption.</summary>
    public static string Caption(int count, int width)
    {
        if (count == 0)
        {
            return "No shortcuts yet";
        }

        var perRow = Math.Min(count, Math.Clamp(width, Group.MinWidth, Group.MaxWidth));
        var rows = (count + perRow - 1) / perRow;
        return $"{count} {(count == 1 ? "app" : "apps")} \u00B7 {rows} {(rows == 1 ? "row" : "rows")}";
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Control);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_tokens is not { } t || Width <= 0 || Height <= 0)
        {
            return;
        }

        var g = e.Graphics;
        var scene = ClientRectangle;
        using var clip = Draw.RoundedRect(new RectangleF(0, 0, scene.Width - 1, scene.Height - 1), LogicalToDeviceUnits(10));
        var oldClip = g.Clip;
        g.SetClip(clip);

        PaintWallpaper(g, scene, t);
        var taskbar = new Rectangle(0, scene.Height - LogicalToDeviceUnits(TaskbarHeight), scene.Width, LogicalToDeviceUnits(TaskbarHeight));
        var groupButton = PaintTaskbar(g, taskbar, t);
        PaintPopup(g, scene, taskbar, groupButton);

        g.Clip = oldClip;
        oldClip.Dispose();
    }

    private void PaintWallpaper(Graphics g, Rectangle scene, ThemeTokens t)
    {
        var stops = t.DesktopGradient;
        using (var sky = new LinearGradientBrush(scene, stops[0], stops[^1], LinearGradientMode.Vertical))
        {
            g.FillRectangle(sky, scene);
        }

        // Three soft hills in the palette's wave colours, back to front.
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var waves = t.Waves;
        for (var i = 0; i < waves.Count; i++)
        {
            var baseY = scene.Height * (0.42f + (0.14f * i));
            var lift = scene.Height * 0.12f;
            using var path = new GraphicsPath();
            path.AddLine(0, scene.Height, 0, baseY);
            path.AddBezier(
                0, baseY,
                scene.Width * (0.25f + (0.1f * i)), baseY - lift,
                scene.Width * (0.6f - (0.1f * i)), baseY + lift,
                scene.Width, baseY - (lift * 0.4f));
            path.AddLine(scene.Width, baseY - (lift * 0.4f), scene.Width, scene.Height);
            path.CloseFigure();
            using var brush = new SolidBrush(waves[i]);
            g.FillPath(brush, path);
        }

        g.SmoothingMode = mode;
    }

    /// <summary>Draws the taskbar strip and returns the group button's bounds.</summary>
    private Rectangle PaintTaskbar(Graphics g, Rectangle bar, ThemeTokens t)
    {
        var dark = _mode == ResolvedMode.Dark;
        using (var fill = new SolidBrush(dark ? TaskbarPalette.Fill : Color.FromArgb(243, 243, 243)))
        {
            g.FillRectangle(fill, bar);
        }

        using (var trim = new SolidBrush(dark ? TaskbarPalette.Trim : Color.FromArgb(224, 224, 224)))
        {
            g.FillRectangle(trim, bar.X, bar.Y, bar.Width, 1);
        }

        var pitch = LogicalToDeviceUnits(44);
        var plate = LogicalToDeviceUnits(40);
        var icon = LogicalToDeviceUnits(24);
        const int buttons = 5;
        var left = bar.X + ((bar.Width - (buttons * pitch) + LogicalToDeviceUnits(4)) / 2);
        var top = bar.Y + ((bar.Height - plate) / 2);
        Rectangle Button(int index) => new(left + (index * pitch), top, plate, plate);
        Rectangle IconBox(Rectangle button) => new(button.X + ((plate - icon) / 2), button.Y + ((plate - icon) / 2), icon, icon);

        // Start: four squares in the Windows blue.
        var start = IconBox(Button(0));
        var half = (icon - LogicalToDeviceUnits(2)) / 2;
        using (var blue = new SolidBrush(Color.FromArgb(0, 120, 212)))
        {
            g.FillRectangle(blue, start.X, start.Y, half, half);
            g.FillRectangle(blue, start.Right - half, start.Y, half, half);
            g.FillRectangle(blue, start.X, start.Bottom - half, half, half);
            g.FillRectangle(blue, start.Right - half, start.Bottom - half, half, half);
        }

        var glyphColor = dark ? Color.FromArgb(220, 220, 220) : Color.FromArgb(40, 40, 40);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
        TextRenderer.DrawText(g, "\uE721", EditorFonts.Icon, Button(1), glyphColor, flags);

        // The group's own button: selected plate plus the accent underline.
        var groupButton = Button(2);
        Draw.RoundedBox(g, groupButton, LogicalToDeviceUnits(4), dark ? TaskbarPalette.Plate : Color.White);
        if (LogoPalette is not null)
        {
            g.DrawImage(PaletteLogos.Square(LogoPalette, icon), IconBox(groupButton));
        }

        var underline = new Rectangle(groupButton.X + ((plate - LogicalToDeviceUnits(16)) / 2), groupButton.Bottom - LogicalToDeviceUnits(3), LogicalToDeviceUnits(16), LogicalToDeviceUnits(3));
        Draw.RoundedBox(g, underline, 1.5f, t.Primary);

        TextRenderer.DrawText(g, Glyphs.Folder, EditorFonts.Icon, Button(3), Color.FromArgb(255, 196, 64), flags);
        TextRenderer.DrawText(g, "\uE756", EditorFonts.Icon, Button(4), glyphColor, flags);
        return groupButton;
    }

    /// <summary>
    /// The group's popup at 100% metrics (the same pixels <see cref="PopupForm"/> draws),
    /// centred over the group button 10px above the taskbar, scaled down only when it would not
    /// fit the scene; drawn with the popup's real transparency.
    /// </summary>
    private void PaintPopup(Graphics g, Rectangle scene, Rectangle taskbar, Rectangle groupButton)
    {
        LastPopupBounds = Rectangle.Empty;
        if (_state.Count <= 0)
        {
            return;
        }

        // The popup's own metrics at this window's DPI, drawn 1:1 like PopupForm draws it; only
        // a popup larger than the scene is scaled down.
        var metrics = PopupMetrics.ForDpi(DeviceDpi);
        var perRow = Math.Clamp(_state.Width, Group.MinWidth, Group.MaxWidth);
        var size = PopupGeometry.SizeFor(_state.Count, perRow, metrics);
        var pad = LogicalToDeviceUnits(ScenePadding);
        var gap = metrics.EdgeOffset;
        var maxWidth = scene.Width - (2 * pad);
        var maxHeight = taskbar.Top - gap - pad;
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)size.Width, maxHeight / (double)size.Height));
        if (scale <= 0 || size.Width <= 0 || size.Height <= 0)
        {
            return; // card too small to show anything
        }

        using var popup = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var pg = Graphics.FromImage(popup))
        using (var painter = new PopupPainter(_state.ColorHex))
        {
            painter.Paint(pg, size, metrics, _state.Count, perRow, -1, _state.IconFor, _state.NameFor, DefaultFont);
        }

        var width = (int)Math.Round(size.Width * scale);
        var height = (int)Math.Round(size.Height * scale);
        var x = Math.Clamp(groupButton.X + (groupButton.Width / 2) - (width / 2), pad, scene.Width - pad - width);
        var y = taskbar.Top - gap - height;
        var target = new Rectangle(x, y, width, height);

        using var attributes = new ImageAttributes();
        var opacity = (float)PopupPainter.OpacityFor(_state.Transparency);
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        var interpolation = g.InterpolationMode;
        g.InterpolationMode = scale < 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        g.DrawImage(popup, target, 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, attributes);
        g.InterpolationMode = interpolation;
        LastPopupBounds = target;
    }
}
