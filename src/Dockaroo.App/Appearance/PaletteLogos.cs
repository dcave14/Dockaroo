using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Appearance;

/// <summary>
/// Crisp palette logos for the editor at any pixel size. <see cref="Icon"/> frames are only sharp
/// at their own sizes (16..256); drawing one at 44 or 55 px stretches the nearest frame. These
/// bitmaps are resampled once from the 256 px frame (bicubic), with the transparent margin
/// trimmed so the tall kangaroo fills its height. Cached per thread (GDI+ bitmaps must not be
/// drawn from two threads at once; the app draws on its one UI thread, tests use many); callers
/// never dispose.
/// </summary>
internal static class PaletteLogos
{
    private const string ResourcePrefix = "Dockaroo.App.Palettes.logo-";

    /// <summary>
    /// Where the kangaroo reads as centred, as a fraction of the art height from its top: the
    /// head-to-feet middle. The thin dark ears (top ~13%) barely show on the dark sidebar, so text
    /// centred on the full art box looks high. Measured on the 1024 px render, 2026-10-16.
    /// </summary>
    public const double VisualCentre = 0.565;

    [ThreadStatic]
    private static Dictionary<(string Palette, int Size, bool Square), Bitmap>? ThreadCache;

    [ThreadStatic]
    private static Dictionary<string, (Bitmap Master, Rectangle Art)>? ThreadMasters;

    /// <summary>
    /// The logo as a <paramref name="box"/> x <paramref name="box"/> bitmap: the art scaled to fill
    /// the box height and centred, transparent at the sides. A drop-in for a square icon slot.
    /// </summary>
    public static Bitmap Square(string paletteId, int box) => Get(paletteId, box, square: true);

    /// <summary>
    /// The logo trimmed to its art, <paramref name="height"/> px tall and as wide as the art's
    /// aspect gives, for places that lay text out next to it.
    /// </summary>
    public static Bitmap Fitted(string paletteId, int height) => Get(paletteId, height, square: false);

    /// <summary>Cache lookup; unknown palettes fall back to the default one.</summary>
    private static Bitmap Get(string paletteId, int size, bool square)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var palette = AppearancePreferences.IsKnownPalette(paletteId) ? paletteId : AppearancePreferences.DefaultPaletteId;
        ThreadCache ??= new();
        if (!ThreadCache.TryGetValue((palette, size, square), out var bitmap))
        {
            bitmap = Render(LoadMaster(palette), size, square);
            ThreadCache[(palette, size, square)] = bitmap;
        }

        return bitmap;
    }

    /// <summary>The palette's 256 px frame and its art rectangle, decoded once per palette.</summary>
    private static (Bitmap Master, Rectangle Art) LoadMaster(string palette)
    {
        ThreadMasters ??= new();
        if (!ThreadMasters.TryGetValue(palette, out var master))
        {
            using var stream = typeof(PaletteLogos).Assembly.GetManifestResourceStream(ResourcePrefix + palette + ".ico")
                ?? throw new InvalidOperationException($"The '{palette}' logo resource is missing.");
            var bitmap = LargestFrame(stream);
            master = (bitmap, ArtBounds(bitmap));
            ThreadMasters[palette] = master;
        }

        return master;
    }

    /// <summary>
    /// The ICO's largest frame as a bitmap. <c>new Icon(stream, 256, 256)</c> is no use here: it
    /// skips the PNG-compressed 256 entry (directory width 0) and returns the 128 frame.
    /// </summary>
    internal static Bitmap LargestFrame(Stream ico)
    {
        using var buffer = new MemoryStream();
        ico.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var count = BitConverter.ToUInt16(bytes, 4);
        int best = -1, bestSize = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            var size = bytes[entry] == 0 ? 256 : bytes[entry];
            if (size > bestSize)
            {
                best = entry;
                bestSize = size;
            }
        }

        if (best < 0)
        {
            throw new InvalidDataException("The logo ICO has no frames.");
        }

        var length = BitConverter.ToInt32(bytes, best + 8);
        var offset = BitConverter.ToInt32(bytes, best + 12);
        var isPng = bytes.AsSpan(offset, 4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        if (!isPng)
        {
            // A classic BMP frame: Icon reads those fine at their exact size.
            buffer.Position = 0;
            using var icon = new Icon(buffer, bestSize, bestSize);
            using var frame = icon.ToBitmap();
            return To32bppArgb(frame);
        }

        using var png = new MemoryStream(bytes, offset, length, writable: false);
        using var decoded = new Bitmap(png);
        return To32bppArgb(decoded);
    }

    /// <summary>
    /// A stream-independent 32bpp ARGB copy (<see cref="ArtBounds"/> reads that layout; a
    /// decoded PNG bitmap also needs its stream kept open).
    /// </summary>
    private static Bitmap To32bppArgb(Bitmap source)
    {
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(copy);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return copy;
    }

    /// <summary>
    /// The smallest rectangle holding every pixel at least <paramref name="minAlpha"/> opaque (whole
    /// image if none). The default skips the faint resampling halo around downscaled art.
    /// </summary>
    internal static Rectangle ArtBounds(Bitmap bitmap, byte minAlpha = 16)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var pixels = new byte[stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (pixels[(y * stride) + (x * 4) + 3] >= minAlpha)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            return right < 0 ? new Rectangle(Point.Empty, bitmap.Size) : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Scales the art into a <paramref name="size"/> box (square canvas or trimmed).</summary>
    private static Bitmap Render((Bitmap Master, Rectangle Art) source, int size, bool square)
    {
        var art = source.Art;
        // Fit the art inside size x size (tall art: full height; wide art: full width).
        var scale = Math.Min((double)size / art.Width, (double)size / art.Height);
        var width = Math.Max(1, (int)Math.Round(art.Width * scale));
        var height = Math.Max(1, (int)Math.Round(art.Height * scale));
        var bitmap = square ? new Bitmap(size, size, PixelFormat.Format32bppPArgb) : new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        var target = new Rectangle((bitmap.Width - width) / 2, (bitmap.Height - height) / 2, width, height);
        using var g = Graphics.FromImage(bitmap);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        // Mirror the source at its edges so the bicubic kernel does not blend in wrapped-around pixels.
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        g.DrawImage(source.Master, target, art.X, art.Y, art.Width, art.Height, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }
}
