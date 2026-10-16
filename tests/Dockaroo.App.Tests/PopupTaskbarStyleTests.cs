using System.Drawing;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Content tests for the taskbar-matched popup: the window sizes fit the real entry count, the
/// palette holds the measured Windows 11 taskbar values, and the Dark-theme paint produces the
/// exact fill and 1px trim pixels.
/// </summary>
public sealed class PopupTaskbarStyleTests
{
    [Fact]
    public void PopupSizes_FitTheRealEntryCount()
    {
        Assert.Equal(new Size(140, 51), PopupGeometry.SizeFor(3, 6));
        Assert.Equal(new Size(184, 95), PopupGeometry.SizeFor(7, 4));
        Assert.Equal(new Size(52, 51), PopupGeometry.SizeFor(1, 10));
    }

    [Fact]
    public void TaskbarPalette_MatchesTheMeasuredTaskbar()
    {
        Assert.Equal(Color.FromArgb(32, 32, 32), TaskbarPalette.Fill);
        Assert.Equal(Color.FromArgb(66, 66, 66), TaskbarPalette.Trim);
        Assert.Equal(Color.FromArgb(46, 46, 46), TaskbarPalette.Plate);
        Assert.Equal(Color.FromArgb(61, 61, 61), TaskbarPalette.PlateTopEdge);
        Assert.Equal(3, TaskbarPalette.PlateRadius);
    }

    [Fact]
    public void PaintedPopup_HasFlatFillAndOnePixelTrim()
    {
        using var bitmap = new Bitmap(140, 66);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            PopupForm.PaintTaskbarBackground(graphics, bitmap.Size);
        }

        var fill = Color.FromArgb(32, 32, 32);
        var trim = Color.FromArgb(66, 66, 66);

        // Interior: flat, deterministic fill (no acrylic).
        Assert.Equal(fill, bitmap.GetPixel(70, 33));

        // Outer edge: the 1px trim, on every side, exactly one pixel thick.
        Assert.Equal(trim, bitmap.GetPixel(70, 0));
        Assert.Equal(trim, bitmap.GetPixel(70, 65));
        Assert.Equal(trim, bitmap.GetPixel(0, 33));
        Assert.Equal(trim, bitmap.GetPixel(139, 33));
        Assert.Equal(fill, bitmap.GetPixel(70, 1));
        Assert.Equal(fill, bitmap.GetPixel(70, 64));
        Assert.Equal(fill, bitmap.GetPixel(1, 33));
        Assert.Equal(fill, bitmap.GetPixel(138, 33));
    }
}
