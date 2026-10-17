using System.Drawing;
using Dockaroo.App.Appearance;
using Xunit;

namespace Dockaroo.App.Tests;

public sealed class PaletteLogosTests
{
    [Theory]
    [InlineData("original")]
    [InlineData("ocean")]
    [InlineData("mint")]
    [InlineData("coral")]
    [InlineData("graphite-gold")]
    public void Square_IsTheExactBox_WithTheArtTouchingTopAndBottom(string palette)
    {
        var logo = PaletteLogos.Square(palette, 44);

        Assert.Equal(new Size(44, 44), logo.Size);
        Assert.True(RowHasInk(logo, 0), $"{palette}: top row is empty, the art does not fill the height");
        Assert.True(RowHasInk(logo, 43), $"{palette}: bottom row is empty, the art does not fill the height");
        // The head art is taller than wide, so the sides of the square stay clear.
        Assert.False(ColumnHasInk(logo, 0), $"{palette}: left column has ink");
        Assert.False(ColumnHasInk(logo, 43), $"{palette}: right column has ink");
    }

    [Fact]
    public void Fitted_IsTrimmedToTheArt()
    {
        var logo = PaletteLogos.Fitted("mint", 60);

        Assert.Equal(60, logo.Height);
        // The head art is near-square (833 x 901 of the master canvas), so 60 px tall is ~55 wide.
        Assert.InRange(logo.Width, 50, 58);
        Assert.True(ColumnHasInk(logo, 0), "left edge is not the art's edge");
        Assert.True(ColumnHasInk(logo, logo.Width - 1), "right edge is not the art's edge");
        Assert.True(RowHasInk(logo, 0), "top edge is not the art's edge");
    }

    [Fact]
    public void Logos_AreCachedPerPaletteAndSize_AndUnknownPalettesFallBackToMint()
    {
        Assert.Same(PaletteLogos.Square("coral", 32), PaletteLogos.Square("coral", 32));
        Assert.NotSame(PaletteLogos.Square("coral", 32), PaletteLogos.Square("coral", 40));
        Assert.NotSame(PaletteLogos.Square("coral", 32), PaletteLogos.Fitted("coral", 32));
        Assert.Same(PaletteLogos.Square("mint", 32), PaletteLogos.Square("no-such-palette", 32));
    }

    [Fact]
    public void Palettes_AreDifferentArt()
    {
        var mint = PaletteLogos.Square("mint", 48);
        var coral = PaletteLogos.Square("coral", 48);
        var differing = 0;
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 48; x++)
            {
                if (mint.GetPixel(x, y) != coral.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        Assert.True(differing > 300, $"mint and coral differ in only {differing} pixels");
    }

    [Fact]
    public void ArtBounds_FindsTheInkOrFallsBackToTheWholeImage()
    {
        using var bitmap = new Bitmap(10, 8);
        Assert.Equal(new Rectangle(0, 0, 10, 8), PaletteLogos.ArtBounds(bitmap));

        bitmap.SetPixel(3, 2, Color.Red);
        bitmap.SetPixel(6, 5, Color.FromArgb(16, 0, 0, 0));
        // Below the alpha threshold: resampling halo, not art.
        bitmap.SetPixel(9, 7, Color.FromArgb(15, 0, 0, 0));
        Assert.Equal(Rectangle.FromLTRB(3, 2, 7, 6), PaletteLogos.ArtBounds(bitmap));
    }

    [Theory]
    [InlineData("original")]
    [InlineData("ocean")]
    [InlineData("mint")]
    [InlineData("coral")]
    [InlineData("graphite-gold")]
    public void ShippedIcons_ExistAndDecodeToTheHeadArt(string palette)
    {
        using var stream = typeof(PaletteLogos).Assembly.GetManifestResourceStream($"Dockaroo.App.Palettes.logo-{palette}.ico");
        Assert.NotNull(stream);
        using var bitmap = PaletteLogos.LargestFrame(stream);

        Assert.Equal(new Size(256, 256), bitmap.Size);

        // Non-empty image: the head's solid ink covers a large part of the frame (~16% of the
        // 256 px frame is at least half opaque; the master canvas keeps wide transparent margins).
        var opaque = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A >= 128)
                {
                    opaque++;
                }
            }
        }

        Assert.True(opaque > 5000, $"{palette}: only {opaque} solid pixels of {bitmap.Width * bitmap.Height}");

        // The gold-kangaroo exports keep the master canvas margins; the head still spans well
        // over half of the frame in each direction (the 256 px frame is not tightly cropped).
        var art = PaletteLogos.ArtBounds(bitmap);
        Assert.True(art.Width >= 150 && art.Height >= 150, $"{palette}: art is only {art.Width}x{art.Height}px of 256");
    }

    [Fact]
    public void BrandInk_IsTheLettersRowsInsideTheLineBox()
    {
        var (top, bottom, lineHeight) = Dockaroo.App.Editor.EditorSidebar.InkRows("Dockaroo", Dockaroo.App.Editor.Theming.EditorFonts.Brand);

        // Real ink, strictly inside the line box (internal leading above, descender space below).
        Assert.InRange(top, 1, lineHeight - 1);
        Assert.InRange(bottom, top + 6, lineHeight - 2);
        Assert.Equal((top, bottom, lineHeight), Dockaroo.App.Editor.EditorSidebar.InkRows("Dockaroo", Dockaroo.App.Editor.Theming.EditorFonts.Brand));
    }

    private static bool RowHasInk(Bitmap bitmap, int y) =>
        Enumerable.Range(0, bitmap.Width).Any(x => bitmap.GetPixel(x, y).A > 0);

    private static bool ColumnHasInk(Bitmap bitmap, int x) =>
        Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 0);
}
