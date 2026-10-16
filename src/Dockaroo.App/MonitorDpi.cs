using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>
/// Effective DPI of a monitor (GetDpiForMonitor). A Win32 query with no file IO, so the popup
/// may call it on the show path. Falls back to 96 when the shell cannot answer.
/// </summary>
internal static class MonitorDpi
{
    private const int MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;

    /// <summary>DPI of the monitor that contains (or is nearest to) <paramref name="point"/>.</summary>
    public static int ForPoint(Point point)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = point.X, Y = point.Y }, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) >= 0 && dpiX > 0)
            {
                return (int)dpiX;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Pre-8.1 shell: no per-monitor DPI, treat as 100%.
        }

        return PopupMetrics.BaseDpi;
    }

    /// <summary>
    /// Icon sizes the popup can ask for on the current monitors: the 100% size plus each
    /// monitor's scaled size. The resident preloads all of them so every popup stays memory-only.
    /// </summary>
    public static IReadOnlyCollection<int> PopupIconSizes()
    {
        var sizes = new SortedSet<int> { PopupMetrics.Default.IconSize };
        foreach (var screen in Screen.AllScreens)
        {
            var center = new Point(
                screen.Bounds.Left + (screen.Bounds.Width / 2),
                screen.Bounds.Top + (screen.Bounds.Height / 2));
            sizes.Add(PopupMetrics.ForDpi(ForPoint(center)).IconSize);
        }

        return sizes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
