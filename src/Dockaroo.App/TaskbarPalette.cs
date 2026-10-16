using System.Drawing;

namespace Dockaroo.App;

/// <summary>
/// The measured colours of the user's Windows 11 taskbar at 100% DPI (task-011 screenshot
/// measurements). The Dark-theme popup reads them so it looks like a piece of the taskbar: a
/// flat body fill, a 1px light-catch trim and the selected/hover button plate. Static readonly
/// and allocation-free; the popup paint path only reads these values.
/// </summary>
internal static class TaskbarPalette
{
    /// <summary>Taskbar body fill (measured rgb(32,32,32)).</summary>
    internal static readonly Color Fill = Color.FromArgb(32, 32, 32);

    /// <summary>1px light-catch trim on the taskbar edge (measured rgb(66,66,66)).</summary>
    internal static readonly Color Trim = Color.FromArgb(66, 66, 66);

    /// <summary>Selected/hover taskbar button plate (measured rgb(46,46,46)).</summary>
    internal static readonly Color Plate = Color.FromArgb(46, 46, 46);

    /// <summary>1px top edge of the selected/hover plate (measured rgb(61,61,61)).</summary>
    internal static readonly Color PlateTopEdge = Color.FromArgb(61, 61, 61);

    /// <summary>Corner radius of the taskbar button plate at 100% (PopupMetrics scales it).</summary>
    internal static readonly int PlateRadius = Dockaroo.Core.Popup.PopupMetrics.BasePlateRadius;
}
