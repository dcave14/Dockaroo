using System.Drawing;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.Core.Tests;

/// <summary>Per-monitor DPI scaling of the popup geometry (backlog P1-5).</summary>
public sealed class PopupMetricsTests
{
    private static readonly Rectangle Bounds = new(0, 0, 2880, 1620);
    private static readonly Rectangle BottomBarWorkArea = new(0, 0, 2880, 1548);

    [Fact]
    public void Default_MatchesThe100PercentConstants()
    {
        var m = PopupMetrics.Default;

        Assert.Equal(1.0, m.Scale);
        Assert.Equal(PopupGeometry.PlateSize, m.PlateSize);
        Assert.Equal(PopupGeometry.PlateGap, m.PlateGap);
        Assert.Equal(PopupGeometry.SidePad, m.SidePad);
        Assert.Equal(PopupGeometry.TopPad, m.TopPad);
        Assert.Equal(PopupGeometry.BottomPad, m.BottomPad);
        Assert.Equal(PopupGeometry.IconSize, m.IconSize);
        Assert.Equal(PopupGeometry.EdgeOffset, m.EdgeOffset);
        Assert.Equal(PopupGeometry.CursorOffset, m.CursorOffset);
        Assert.Equal(PopupGeometry.CellPitch, m.CellPitch);
        Assert.Equal(3, m.PlateRadius);
        Assert.Same(PopupMetrics.Default, PopupMetrics.ForDpi(96));
    }

    [Theory]
    // dpi, plate, gap, side, top, bottom, icon, radius, edge
    [InlineData(120, 50, 5, 8, 6, 8, 30, 4, 13)]
    [InlineData(144, 60, 6, 9, 8, 9, 36, 5, 15)]
    [InlineData(192, 80, 8, 12, 10, 12, 48, 6, 20)]
    public void ForDpi_ScalesEveryMeasurement(int dpi, int plate, int gap, int side, int top, int bottom, int icon, int radius, int edge)
    {
        var m = PopupMetrics.ForDpi(dpi);

        Assert.Equal(dpi / 96.0, m.Scale);
        Assert.Equal(plate, m.PlateSize);
        Assert.Equal(gap, m.PlateGap);
        Assert.Equal(side, m.SidePad);
        Assert.Equal(top, m.TopPad);
        Assert.Equal(bottom, m.BottomPad);
        Assert.Equal(icon, m.IconSize);
        Assert.Equal(radius, m.PlateRadius);
        Assert.Equal(edge, m.EdgeOffset);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-5, 1.0)]
    [InlineData(48, 1.0)]
    [InlineData(960, 5.0)]
    public void ForDpi_ClampsToTheWindowsRange(int dpi, double scale)
    {
        Assert.Equal(scale, PopupMetrics.ForDpi(dpi).Scale);
    }

    [Fact]
    public void For_NaN_IsTreatedAs100Percent()
    {
        Assert.Same(PopupMetrics.Default, PopupMetrics.For(double.NaN));
    }

    [Theory]
    [InlineData(96, 140, 51)]
    [InlineData(144, 210, 77)]
    [InlineData(192, 280, 102)]
    public void SizeFor_ThreeApps_ScalesWithDpi(int dpi, int width, int height)
    {
        Assert.Equal(new Size(width, height), PopupGeometry.SizeFor(3, 6, PopupMetrics.ForDpi(dpi)));
    }

    [Fact]
    public void SizeFor_TwoRowsAt150Percent_UsesTheScaledRowPitch()
    {
        // 7 apps, 4 per row: 9+4*60+3*6+9 = 276 wide, 8+2*60+6+9 = 143 tall.
        Assert.Equal(new Size(276, 143), PopupGeometry.SizeFor(7, 4, PopupMetrics.ForDpi(144)));
    }

    [Fact]
    public void TileBoundsAndHitTest_AgreeAt150Percent()
    {
        var m = PopupMetrics.ForDpi(144);

        var tile = PopupGeometry.TileBounds(4, 3, m);

        Assert.Equal(new Rectangle(75, 74, 60, 60), tile);
        Assert.Equal(4, PopupGeometry.HitTest(new Point(tile.Left, tile.Top), 7, 3, m));
        Assert.Equal(4, PopupGeometry.HitTest(new Point(tile.Right - 1, tile.Bottom - 1), 7, 3, m));
        Assert.Equal(-1, PopupGeometry.HitTest(new Point(tile.Right, tile.Top), 7, 3, m)); // in the 6px gap
        Assert.Equal(-1, PopupGeometry.HitTest(new Point(tile.Left, tile.Bottom), 7, 3, m));
    }

    [Fact]
    public void Place_BottomTaskbarAt150Percent_UsesTheScaledEdgeOffset()
    {
        var m = PopupMetrics.ForDpi(144);
        var size = PopupGeometry.SizeFor(3, 6, m);

        var location = PopupGeometry.Place(Bounds, BottomBarWorkArea, new Point(1440, 1590), size, m);

        Assert.Equal(new Point(1440 - (210 / 2), 1548 - 77 - 15), location);
    }

    [Fact]
    public void Place_NearRightEdgeAt200Percent_ClampsWithTheScaledOffset()
    {
        var m = PopupMetrics.ForDpi(192);
        var size = PopupGeometry.SizeFor(3, 6, m);

        var location = PopupGeometry.Place(Bounds, BottomBarWorkArea, new Point(2870, 1590), size, m);

        Assert.Equal(2880 - 280 - 20, location.X);
        Assert.True(BottomBarWorkArea.Contains(new Rectangle(location, size)));
    }

    [Fact]
    public void FitPerRow_At200Percent_DropsColumnsSoonerThanAt100()
    {
        // 20 per row is 888px at 100% but 1776px at 200%; a 1366px-wide 200% work area fits
        // (1366 - 40 - 24 + 8) / 88 = 14 columns.
        var workArea = new Rectangle(0, 0, 1366, 1000);

        Assert.Equal(20, PopupGeometry.FitPerRow(20, 20, workArea));
        var perRow = PopupGeometry.FitPerRow(20, 20, workArea, PopupMetrics.ForDpi(192));

        Assert.Equal(14, perRow);
        Assert.True(PopupGeometry.SizeFor(20, perRow, PopupMetrics.ForDpi(192)).Width <= 1366 - 40);
    }
}
