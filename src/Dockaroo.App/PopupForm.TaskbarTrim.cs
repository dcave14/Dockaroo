using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>
/// Keeps the bar-side trim right while a taskbar popup is open. The popup opens tucked under the
/// clicked bar with that strip clipped away (one trim line, the bar's). An auto-hide bar can slide
/// away while the popup stays put; then the clip is dropped so the popup's own trim shows instead
/// of a cut-off edge, and it is restored if the bar comes back. The popup itself never moves.
/// </summary>
internal sealed partial class PopupForm
{
    // How often the clicked bar's rectangle is re-read while the popup is open (no file IO).
    private const int BarWatchIntervalMs = 100;

    // The visible part while tucked (popup coordinates); null for non-taskbar opens.
    private readonly Rectangle? _tuckClip;
    private bool _tuckClipped;
    private System.Windows.Forms.Timer? _barWatch;

    /// <summary>True while the tucked strip is clipped away (asserted by tests).</summary>
    internal bool TuckClipped => _tuckClipped;

    /// <summary>
    /// Applies the clip for the bar's current rectangle: clipped while the bar covers the strip,
    /// the full popup (trim included) once it does not. Tests pass the bar rectangle directly.
    /// </summary>
    internal void UpdateTuckClip(Rectangle bar)
    {
        if (_tuckClip is null)
        {
            return;
        }

        var covered = PopupGeometry.BarCoversTuck(Bounds, bar);
        if (covered != _tuckClipped)
        {
            ApplyTuckClip(covered);
        }
    }

    /// <summary>
    /// Clips the tucked strip away or shows the whole popup. A fresh Region each time: WinForms
    /// disposes the previous one when it is replaced, so a Region cannot be re-applied.
    /// </summary>
    private void ApplyTuckClip(bool clipped)
    {
        _tuckClipped = clipped && _tuckClip is not null;
        Region = _tuckClipped ? new Region(_tuckClip!.Value) : null;
        Invalidate();
    }

    private void StartBarWatch()
    {
        if (_barWindow == IntPtr.Zero)
        {
            return;
        }

        _barWatch = new System.Windows.Forms.Timer { Interval = BarWatchIntervalMs };
        _barWatch.Tick += (_, _) =>
        {
            if (GetWindowRect(_barWindow, out var rect))
            {
                UpdateTuckClip(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            }
        };
        _barWatch.Start();
    }

    private void StopBarWatch()
    {
        _barWatch?.Stop();
        _barWatch?.Dispose();
        _barWatch = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
