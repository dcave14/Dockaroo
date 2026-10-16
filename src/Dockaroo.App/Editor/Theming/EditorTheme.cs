using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Theming;

/// <summary>
/// What the editor needs from the app appearance: the tokens in effect, the mode (for the title
/// bar and scrollbars), the palette logo, and a change notification raised on the UI thread.
/// <see cref="Appearance.ThemeService"/> implements it; tests use <see cref="StaticThemeSource"/>.
/// </summary>
internal interface IEditorThemeSource
{
    ThemeTokens Tokens { get; }

    ResolvedMode EffectiveMode { get; }

    /// <summary>The palette in effect; editor logos are drawn from <see cref="Appearance.PaletteLogos"/> with it.</summary>
    string PaletteId { get; }

    /// <summary>The palette logo at a size; owned by the source, never disposed by callers.</summary>
    Icon GetAppIcon(Size size);

    event EventHandler? Changed;
}

/// <summary>
/// Writable app appearance for the editor's Appearance page. <see cref="Appearance.ThemeService"/>
/// implements it; a failed save returns the error and keeps the previous choice.
/// </summary>
internal interface IAppearanceSettings
{
    AppearancePreferences Preferences { get; }

    string? SetMode(AppMode mode);

    string? SetPalette(string paletteId);

    /// <summary>Any palette's logo at a size; owned by the settings, never disposed by callers.</summary>
    Icon GetIcon(string paletteId, Size size);
}

/// <summary>A fixed palette and mode (tests, harnesses, standalone editors).</summary>
internal sealed class StaticThemeSource : IEditorThemeSource, IDisposable
{
    private readonly string _paletteId;
    private readonly Dictionary<int, Icon> _icons = new();

    public StaticThemeSource(string paletteId, ResolvedMode mode)
    {
        _paletteId = AppearancePreferences.IsKnownPalette(paletteId) ? paletteId : AppearancePreferences.DefaultPaletteId;
        EffectiveMode = mode;
        Tokens = ThemeCatalog.Default.Get(_paletteId).For(mode);
    }

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public ThemeTokens Tokens { get; }

    public ResolvedMode EffectiveMode { get; }

    public string PaletteId => _paletteId;

    public Icon GetAppIcon(Size size)
    {
        if (!_icons.TryGetValue(size.Width, out var icon))
        {
            using var stream = typeof(StaticThemeSource).Assembly.GetManifestResourceStream($"Dockaroo.App.Palettes.logo-{_paletteId}.ico")
                ?? throw new InvalidOperationException($"The '{_paletteId}' logo resource is missing.");
            icon = new Icon(stream, size);
            _icons[size.Width] = icon;
        }

        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }
}

/// <summary>Implemented by editor controls that repaint from the current tokens.</summary>
internal interface IThemedControl
{
    void ApplyTheme(ThemeTokens tokens, ResolvedMode mode);
}

/// <summary>
/// Fonts from the design spec (Segoe UI Variable, falling back to Segoe UI). Sizes are the
/// spec's 96-DPI pixel sizes expressed in points, so WinForms scales them per monitor.
/// </summary>
internal static class EditorFonts
{
    private static readonly string TextFamily = FirstInstalled("Segoe UI Variable Text", "Segoe UI");
    private static readonly string DisplayFamily = FirstInstalled("Segoe UI Variable Display", "Segoe UI");

    /// <summary>Glyph font: Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10.</summary>
    public static readonly string IconFamily = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");

    public static Font Title { get; } = new(DisplayFamily, 22.5f, FontStyle.Bold);

    public static Font Section { get; } = new(DisplayFamily, 13.5f, FontStyle.Bold);

    public static Font Body { get; } = new(TextFamily, 10.5f);

    public static Font BodyStrong { get; } = new(TextFamily, 10.5f, FontStyle.Bold);

    public static Font Label { get; } = new(TextFamily, 9f);

    public static Font LabelStrong { get; } = new(TextFamily, 9f, FontStyle.Bold);

    public static Font Micro { get; } = new(TextFamily, 8.25f);

    public static Font Brand { get; } = new(DisplayFamily, 13.5f, FontStyle.Bold);

    public static Font Icon { get; } = new(IconFamily, 10.5f);

    public static Font IconSmall { get; } = new(IconFamily, 9f);

    private static string FirstInstalled(params string[] families)
    {
        foreach (var name in families)
        {
            try
            {
                using var family = new FontFamily(name);
                return family.Name;
            }
            catch (ArgumentException)
            {
                // Not installed on this Windows version.
            }
        }

        return FontFamily.GenericSansSerif.Name;
    }
}

/// <summary>Glyph code points shared by Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
internal static class Glyphs
{
    public const string Add = "\uE710";
    public const string More = "\uE712";
    public const string Delete = "\uE74D";
    public const string Moon = "\uE708";
    public const string Sun = "\uE706";
    public const string Color = "\uE790";
    public const string Download = "\uE896";
    public const string Folder = "\uE8B7";
    public const string CheckMark = "\uE73E";
    public const string Minus = "\uE738";
    public const string System = "\uE770";
    public const string Picture = "\uE8B9";
    public const string Search = "\uE721";
    public const string CommandPrompt = "\uE756";
    public const string MiddleDot = "\u00B7";
}

/// <summary>Native visual styles that follow the app mode.</summary>
internal static class NativeTheme
{
    /// <summary>Dark scrollbars in dark mode (Explorer's dark visual style); no-op without a handle.</summary>
    public static void ApplyScrollbars(Control control, ResolvedMode mode)
    {
        if (!control.IsHandleCreated)
        {
            return;
        }

        try
        {
            _ = SetWindowTheme(control.Handle, mode == ResolvedMode.Dark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No themed scrollbars on this Windows build; the default ones still work.
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);
}

/// <summary>Small drawing helpers for the owner-drawn controls.</summary>
internal static class Draw
{
    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fills (and optionally strokes, 1px inside) a rounded rectangle, anti-aliased.</summary>
    public static void RoundedBox(Graphics g, Rectangle bounds, float radius, Color fill, Color? border = null, float borderWidth = 1f)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = border is null ? 0f : borderWidth / 2f;
        var rect = new RectangleF(bounds.X + inset, bounds.Y + inset, bounds.Width - (2 * inset) - 1 + (border is null ? 1 : 0), bounds.Height - (2 * inset) - 1 + (border is null ? 1 : 0));
        using (var path = RoundedRect(rect, radius))
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
            if (border is { } borderColor)
            {
                using var pen = new Pen(borderColor, borderWidth);
                g.DrawPath(pen, path);
            }
        }

        g.SmoothingMode = mode;
    }

    /// <summary>Linear blend of two colours (amount 0 = a, 1 = b).</summary>
    public static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.R + ((b.R - a.R) * amount)),
            (int)Math.Round(a.G + ((b.G - a.G) * amount)),
            (int)Math.Round(a.B + ((b.B - a.B) * amount)));
    }
}
