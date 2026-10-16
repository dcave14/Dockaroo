using System.Drawing;
using System.Drawing.Imaging;
using Dockaroo.Core.Icons;
using Xunit;

namespace Dockaroo.Core.Tests;

/// <summary>The .ico encoder behind the group "Change icon" picker (task-016).</summary>
public sealed class IcoWriterTests
{
    [Fact]
    public void Write_ProducesAValidDirectoryWithEveryDefaultSize()
    {
        using var source = Solid(300, 300, Color.FromArgb(255, 20, 140, 120));

        var bytes = IcoWriter.Write(source);

        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.Equal(IcoWriter.DefaultSizes.Count, count);
        var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + (16 * i)] == 0 ? 256 : bytes[6 + (16 * i)]).ToArray();
        Assert.Equal(IcoWriter.DefaultSizes, sizes);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            Assert.Equal(32, BitConverter.ToUInt16(bytes, entry + 6)); // bpp
            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            Assert.True(offset + length <= bytes.Length);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Skip(offset).Take(4)); // PNG frame
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(128)]
    public void Write_FramesLoadBackThroughSystemDrawingWithTheSourceColour(int size)
    {
        using var source = Solid(64, 64, Color.FromArgb(255, 200, 40, 60));
        using var stream = new MemoryStream(IcoWriter.Write(source));

        using var icon = new Icon(stream, new Size(size, size));
        using var bitmap = icon.ToBitmap();

        Assert.Equal(new Size(size, size), bitmap.Size);
        var centre = bitmap.GetPixel(size / 2, size / 2);
        Assert.Equal(255, centre.A);
        Assert.InRange(centre.R, 195, 205);
        Assert.InRange(centre.G, 35, 45);
        Assert.InRange(centre.B, 55, 65);
    }

    [Fact]
    public void Write_The256Frame_IsAFull256PixelPng()
    {
        // System.Drawing.Icon never selects 256px entries (stored as width 0); the shell does.
        using var source = Solid(64, 64, Color.FromArgb(255, 200, 40, 60));
        var bytes = IcoWriter.Write(source);
        var last = 6 + (16 * (IcoWriter.DefaultSizes.Count - 1));
        Assert.Equal(0, bytes[last]); // 256
        var length = BitConverter.ToInt32(bytes, last + 8);
        var offset = BitConverter.ToInt32(bytes, last + 12);

        using var png = new MemoryStream(bytes, offset, length);
        using var bitmap = new Bitmap(png);

        Assert.Equal(new Size(256, 256), bitmap.Size);
        Assert.Equal(Color.FromArgb(255, 200, 40, 60).ToArgb(), bitmap.GetPixel(128, 128).ToArgb());
    }

    [Fact]
    public void Write_WideSourceIsCentredWithTransparentBands()
    {
        using var source = Solid(200, 100, Color.Blue);
        using var stream = new MemoryStream(IcoWriter.Write(source, new[] { 64 }));

        using var icon = new Icon(stream, new Size(64, 64));
        using var bitmap = icon.ToBitmap();

        Assert.Equal(0, bitmap.GetPixel(32, 2).A);   // band above
        Assert.Equal(0, bitmap.GetPixel(32, 61).A);  // band below
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(32, 32).ToArgb());
        Assert.Equal(Color.Blue.ToArgb(), bitmap.GetPixel(1, 32).ToArgb()); // full width, no dark fringe
    }

    [Fact]
    public void Write_KeepsSourceTransparency()
    {
        using var source = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.Red, 8, 8, 16, 16);
        }

        using var stream = new MemoryStream(IcoWriter.Write(source, new[] { 32 }));
        using var icon = new Icon(stream, new Size(32, 32));
        using var bitmap = icon.ToBitmap();

        Assert.Equal(0, bitmap.GetPixel(2, 2).A);
        Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(16, 16).ToArgb());
    }

    [Fact]
    public void Write_RejectsBadSizes()
    {
        using var source = Solid(8, 8, Color.Black);

        Assert.Throws<ArgumentException>(() => IcoWriter.Write(source, Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => IcoWriter.Write(source, new[] { 512 }));
    }

    private static Bitmap Solid(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }
}
