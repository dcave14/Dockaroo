namespace Dockaroo.Core.Popup;

/// <summary>
/// Popup measurements in physical pixels for one monitor scale. The 100% values are the
/// measured Windows 11 taskbar metrics (<see cref="PopupGeometry"/> constants); other scales
/// multiply them and round half away from zero, so 150% gives 60px plates and 36px icons.
/// Pure math, no IO: the popup derives it from the anchor monitor's DPI on the show path.
/// </summary>
public sealed record PopupMetrics
{
    /// <summary>Logical DPI that equals 100% scale.</summary>
    public const int BaseDpi = 96;

    /// <summary>Smallest accepted scale factor (Windows offers 100% as its minimum).</summary>
    public const double MinScale = 1.0;

    /// <summary>Largest accepted scale factor (Windows tops out at 500%).</summary>
    public const double MaxScale = 5.0;

    /// <summary>Base corner radius of an icon plate at 100% (measured taskbar button).</summary>
    public const int BasePlateRadius = 3;

    private PopupMetrics(double scale)
    {
        Scale = scale;
        PlateSize = Scaled(PopupGeometry.PlateSize, scale);
        PlateGap = Scaled(PopupGeometry.PlateGap, scale);
        SidePad = Scaled(PopupGeometry.SidePad, scale);
        TopPad = Scaled(PopupGeometry.TopPad, scale);
        BottomPad = Scaled(PopupGeometry.BottomPad, scale);
        IconSize = Scaled(PopupGeometry.IconSize, scale);
        PlateRadius = Scaled(BasePlateRadius, scale);
        EdgeOffset = Scaled(PopupGeometry.EdgeOffset, scale);
        CursorOffset = Scaled(PopupGeometry.CursorOffset, scale);
        HiddenTaskbarBand = Scaled(PopupGeometry.HiddenTaskbarBand, scale);
        HiddenTaskbarOffset = Scaled(PopupGeometry.HiddenTaskbarOffset, scale);
        TaskbarTuck = Scaled(PopupGeometry.TaskbarTuck, scale);
    }

    /// <summary>The 100% metrics; identical to the <see cref="PopupGeometry"/> constants.</summary>
    public static PopupMetrics Default { get; } = new(1.0);

    /// <summary>Scale factor these metrics were built for (1.0 = 96 DPI).</summary>
    public double Scale { get; }

    /// <summary>Icon plate edge.</summary>
    public int PlateSize { get; }

    /// <summary>Gap between plates and between rows.</summary>
    public int PlateGap { get; }

    /// <summary>Left and right padding.</summary>
    public int SidePad { get; }

    /// <summary>Top padding.</summary>
    public int TopPad { get; }

    /// <summary>Bottom padding.</summary>
    public int BottomPad { get; }

    /// <summary>Icon edge inside a plate; also the icon cache size the popup reads.</summary>
    public int IconSize { get; }

    /// <summary>Plate corner radius.</summary>
    public int PlateRadius { get; }

    /// <summary>Gap between the popup and a taskbar or working-area edge.</summary>
    public int EdgeOffset { get; }

    /// <summary>Gap above a click point that was not on the taskbar.</summary>
    public int CursorOffset { get; }

    /// <summary>Bottom band that counts as a click on a hidden taskbar.</summary>
    public int HiddenTaskbarBand { get; }

    /// <summary>Gap above the bottom edge for clicks in the hidden-taskbar band.</summary>
    public int HiddenTaskbarOffset { get; }

    /// <summary>How far a taskbar popup tucks under the bar (trim + 1px of padding at 100%).</summary>
    public int TaskbarTuck { get; }

    /// <summary>Distance from one plate to the next, both axes.</summary>
    public int CellPitch => PlateSize + PlateGap;

    /// <summary>Metrics for a scale factor; clamped to 100%..500%, 100% returns <see cref="Default"/>.</summary>
    public static PopupMetrics For(double scale)
    {
        if (double.IsNaN(scale))
        {
            scale = MinScale;
        }

        scale = Math.Clamp(scale, MinScale, MaxScale);
        return scale == 1.0 ? Default : new PopupMetrics(scale);
    }

    /// <summary>Metrics for a monitor DPI (96 = 100%, 144 = 150%, 192 = 200%).</summary>
    public static PopupMetrics ForDpi(int dpi)
    {
        return For(dpi <= 0 ? MinScale : (double)dpi / BaseDpi);
    }

    private static int Scaled(int value, double scale)
    {
        return Math.Max(1, (int)Math.Round(value * scale, MidpointRounding.AwayFromZero));
    }
}
