using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Dockaroo.App;

/// <summary>
/// Per-pixel alpha: the popup is a layered window fed by <c>UpdateLayeredWindow</c>, so the
/// group's transparency fades only the surface while icons stay opaque (<c>Form.Opacity</c>
/// faded the whole window, icons included). WM_PAINT does not reach the screen for such a
/// window, so it is validated without painting; every visual change goes through
/// <see cref="UpdateLayer"/>. <see cref="OnPaint"/> still serves WM_PRINT (PrintWindow and
/// DrawToBitmap captures, which show the surface over the opaque back colour).
/// </summary>
internal sealed partial class PopupForm
{
    private const int WsExLayered = 0x00080000;

    /// <summary>Whether the last <see cref="UpdateLayer"/> reached the window (test hook).</summary>
    internal bool LayerUpToDate { get; private set; }

    /// <summary>
    /// The popup frame as a 32bpp premultiplied bitmap: surface at the group's alpha, hover plate
    /// and icons opaque, the tucked strip transparent while it is clipped. Caller disposes.
    /// </summary>
    internal Bitmap RenderLayer()
    {
        var size = _popupSize;
        var frame = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(frame);
        _painter.Paint(g, size, _metrics, _group.Shortcuts.Count, _perRow, _hoveredIndex, IconFor, i => _group.Shortcuts[i].Name, Font);
        if (_tuckClipped && _tuckClip is { } visible)
        {
            // The window Region already hides the strip; clearing it too keeps the layer honest.
            using var outside = new Region(new Rectangle(Point.Empty, size));
            outside.Exclude(visible);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var clear = new SolidBrush(Color.Transparent);
            g.FillRegion(clear, outside);
        }

        return frame;
    }

    /// <summary>
    /// Pushes a freshly rendered frame to the layered window (memory only, no IO). A failure
    /// leaves the previous frame up and is reported through <see cref="LayerUpToDate"/>.
    /// </summary>
    private void UpdateLayer()
    {
        LayerUpToDate = false;
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        using var frame = RenderLayer();
        IntPtr screenDc = IntPtr.Zero, memoryDc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = frame.GetHbitmap(Color.FromArgb(0));
            previous = SelectObject(memoryDc, bitmap);
            var size = new NativeSize { Width = frame.Width, Height = frame.Height };
            var source = new NativePoint();
            var blend = new BlendFunction { BlendOp = AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            LayerUpToDate = UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                _ = ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>The layered window's WM_PAINT: nothing to draw (frames arrive through ULW).</summary>
    private bool SkipLayeredPaint(ref Message m)
    {
        const int WmPaint = 0x000F;
        if (m.Msg != WmPaint || !IsHandleCreated)
        {
            return false;
        }

        _ = ValidateRect(Handle, IntPtr.Zero);
        m.Result = IntPtr.Zero;
        return true;
    }

    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private const int UlwAlpha = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, IntPtr destinationPoint, ref NativeSize size, IntPtr sourceDc, ref NativePoint sourcePoint, int colorKey, ref BlendFunction blend, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ValidateRect(IntPtr window, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
