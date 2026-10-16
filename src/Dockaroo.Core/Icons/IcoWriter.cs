using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Dockaroo.Core.Icons;

/// <summary>
/// Builds a multi-size Windows .ico from any bitmap (the group "Change icon" picker): every frame
/// is the source fitted into a transparent square, centred, high-quality resampled and stored
/// as 32-bit PNG data (supported by Windows since Vista). Pure in-memory; callers write the bytes.
/// </summary>
public static class IcoWriter
{
    /// <summary>Frame sizes written by default: the shell's small, taskbar, list and jumbo sizes.</summary>
    public static readonly IReadOnlyList<int> DefaultSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    /// <summary>Encodes <paramref name="source"/> as an .ico with one frame per size (1..256).</summary>
    public static byte[] Write(Image source, IReadOnlyList<int>? sizes = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        sizes ??= DefaultSizes;
        if (sizes.Count == 0 || sizes.Any(size => size is < 1 or > 256))
        {
            throw new ArgumentException("Icon sizes must be between 1 and 256.", nameof(sizes));
        }

        var frames = sizes.Distinct().OrderBy(size => size).Select(size => (Size: size, Png: RenderFrame(source, size))).ToList();

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);

        // ICONDIR: reserved, type 1 (icon), image count.
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);

        var offset = 6 + (16 * frames.Count);
        foreach (var (size, png) in frames)
        {
            // ICONDIRENTRY: width/height (0 means 256), colours, reserved, planes, bpp, size, offset.
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(offset);
            offset += png.Length;
        }

        foreach (var (_, png) in frames)
        {
            writer.Write(png);
        }

        writer.Flush();
        return output.ToArray();
    }

    private static byte[] RenderFrame(Image source, int size)
    {
        using var frame = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.HighQuality;

            // Fit and centre, keeping the aspect ratio (a wide logo gets transparent bands).
            var scale = Math.Min((double)size / source.Width, (double)size / source.Height);
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var target = new Rectangle((size - width) / 2, (size - height) / 2, width, height);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe from sampling outside the source
            graphics.DrawImage(source, target, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }

        using var png = new MemoryStream();
        frame.Save(png, ImageFormat.Png);
        return png.ToArray();
    }
}
