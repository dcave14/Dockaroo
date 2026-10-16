using System.Drawing;
using Dockaroo.Core;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class PopupGeometryTests
{
    private static readonly Rectangle Bounds = new(0, 0, 1920, 1080);
    private static readonly Size TwoRowPopup = new(272, 125);

    // ---------------------------------------------------------------- size and grid

    [Fact]
    public void SizeFor_TwelveEntriesSixWide_IsTwoRows()
    {
        Assert.Equal(new Size(272, 95), PopupGeometry.SizeFor(12, 6));
    }

    [Fact]
    public void SizeFor_RoundsUpRows()
    {
        Assert.Equal(new Size(272, 95), PopupGeometry.SizeFor(7, 6));
        Assert.Equal(new Size(272, 51), PopupGeometry.SizeFor(6, 6));
        Assert.Equal(new Size(52, 227), PopupGeometry.SizeFor(5, 1));
    }

    [Fact]
    public void SizeFor_ContentFitsTheRealEntryCount()
    {
        // The window fits the real cells instead of reserving Width columns: 3 apps at
        // Width 6 used to be 480x80 with dead columns; it is 140x51 now.
        Assert.Equal(new Size(140, 51), PopupGeometry.SizeFor(3, 6));
        Assert.Equal(new Size(184, 95), PopupGeometry.SizeFor(7, 4));
        Assert.Equal(new Size(52, 51), PopupGeometry.SizeFor(1, 10));
    }

    [Fact]
    public void SizeFor_NoEntries_IsEmpty()
    {
        Assert.Equal(Size.Empty, PopupGeometry.SizeFor(0, 6));
    }

    [Fact]
    public void SizeFor_ClampsWidthToModelBounds()
    {
        Assert.Equal(PopupGeometry.SizeFor(5, Group.MinWidth), PopupGeometry.SizeFor(5, 0));
        Assert.Equal(PopupGeometry.SizeFor(5, Group.MaxWidth), PopupGeometry.SizeFor(5, 99));
    }

    [Fact]
    public void TileBounds_SecondRowStartsBelowTheFirst()
    {
        Assert.Equal(new Rectangle(6, 49, 40, 40), PopupGeometry.TileBounds(6, 6));
        Assert.Equal(new Rectangle(226, 5, 40, 40), PopupGeometry.TileBounds(5, 6));
    }

    [Fact]
    public void HitTest_MapsPointsToEntries()
    {
        Assert.Equal(0, PopupGeometry.HitTest(new Point(10, 10), 12, 6));
        Assert.Equal(7, PopupGeometry.HitTest(new Point(85, 85), 12, 6));
        Assert.Equal(-1, PopupGeometry.HitTest(new Point(481, 0), 12, 6));
        Assert.Equal(-1, PopupGeometry.HitTest(new Point(10, 10), 0, 6));
    }

    // ---------------------------------------------------------------- taskbar edge and rectangle

    [Fact]
    public void TaskbarRectFor_BottomTaskbar()
    {
        var workingArea = new Rectangle(0, 0, 1920, 1040);

        Assert.Equal(new Rectangle(0, 1040, 1920, 40), PopupGeometry.TaskbarRectFor(Bounds, workingArea));
        Assert.Equal(TaskbarEdge.Bottom, PopupGeometry.EdgeFor(Bounds, workingArea));
    }

    [Fact]
    public void TaskbarRectFor_TopTaskbar()
    {
        var workingArea = new Rectangle(0, 40, 1920, 1040);

        Assert.Equal(new Rectangle(0, 0, 1920, 40), PopupGeometry.TaskbarRectFor(Bounds, workingArea));
        Assert.Equal(TaskbarEdge.Top, PopupGeometry.EdgeFor(Bounds, workingArea));
    }

    [Fact]
    public void TaskbarRectFor_LeftTaskbar()
    {
        var workingArea = new Rectangle(40, 0, 1880, 1080);

        Assert.Equal(new Rectangle(0, 0, 40, 1080), PopupGeometry.TaskbarRectFor(Bounds, workingArea));
        Assert.Equal(TaskbarEdge.Left, PopupGeometry.EdgeFor(Bounds, workingArea));
    }

    [Fact]
    public void TaskbarRectFor_RightTaskbar()
    {
        var workingArea = new Rectangle(0, 0, 1880, 1080);

        Assert.Equal(new Rectangle(1880, 0, 40, 1080), PopupGeometry.TaskbarRectFor(Bounds, workingArea));
        Assert.Equal(TaskbarEdge.Right, PopupGeometry.EdgeFor(Bounds, workingArea));
    }

    [Fact]
    public void EdgeAndRect_NoTaskbar_AreNoneAndEmpty()
    {
        Assert.Equal(TaskbarEdge.None, PopupGeometry.EdgeFor(Bounds, Bounds));
        Assert.Equal(Rectangle.Empty, PopupGeometry.TaskbarRectFor(Bounds, Bounds));
    }

    // ---------------------------------------------------------------- placement per edge

    [Fact]
    public void Place_ClickOnBottomTaskbar_OpensAboveTheBarCentered()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 0, 1920, 1040), new Point(960, 1060), TwoRowPopup);

        Assert.Equal(new Point(824, 905), location);
    }

    [Fact]
    public void Place_ClickOnTopTaskbar_OpensBelowTheBarCentered()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 40, 1920, 1040), new Point(500, 20), TwoRowPopup);

        Assert.Equal(new Point(364, 50), location);
    }

    [Fact]
    public void Place_ClickOnLeftTaskbar_OpensRightOfTheBarCentered()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(40, 0, 1880, 1080), new Point(20, 500), TwoRowPopup);

        Assert.Equal(new Point(50, 438), location);
    }

    [Fact]
    public void Place_ClickOnRightTaskbar_OpensLeftOfTheBarCentered()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 0, 1880, 1080), new Point(1900, 500), TwoRowPopup);

        Assert.Equal(new Point(1598, 438), location);
    }

    [Fact]
    public void Place_ClickNotOnTaskbar_OpensAboveTheClickPoint()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 0, 1920, 1040), new Point(960, 500), TwoRowPopup);

        Assert.Equal(new Point(824, 355), location);
    }

    // ---------------------------------------------------------------- clamping

    [Fact]
    public void Place_NearLeftEdge_ClampsInsideTheScreen()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 0, 1920, 1040), new Point(40, 1060), TwoRowPopup);

        Assert.Equal(new Point(10, 905), location);
    }

    [Fact]
    public void Place_NearRightEdge_ClampsInsideTheScreen()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 0, 1920, 1040), new Point(1900, 1060), TwoRowPopup);

        Assert.Equal(new Point(1638, 905), location);
    }

    [Fact]
    public void Place_NearTopEdge_ClampsInsideTheScreen()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 0, 1920, 1040), new Point(960, 30), TwoRowPopup);

        Assert.Equal(new Point(824, 10), location);
    }

    [Fact]
    public void Place_ClampedPopupOverTopTaskbar_IsPushedBelowIt()
    {
        // Not on the bar, so it opens above the click point and the working-area clamp
        // keeps it below the top bar.
        var location = PopupGeometry.Place(Bounds, new Rectangle(0, 40, 1920, 1040), new Point(960, 45), TwoRowPopup);

        Assert.Equal(new Point(824, 50), location);
    }

    [Fact]
    public void Place_ClampedPopupOverLeftTaskbar_IsPushedRightOfIt()
    {
        var location = PopupGeometry.Place(Bounds, new Rectangle(40, 0, 1880, 1080), new Point(100, 20), TwoRowPopup);

        Assert.Equal(new Point(50, 10), location);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Place_SideTaskbarCursorNearBottom_StaysAboveTheWorkingAreaBottom(bool leftBar)
    {
        var workArea = leftBar ? new Rectangle(48, 0, 1872, 1080) : new Rectangle(0, 0, 1872, 1080);
        var cursor = new Point(leftBar ? 20 : 1900, 1060);

        var location = PopupGeometry.Place(Bounds, workArea, cursor, TwoRowPopup);

        Assert.Equal(1080 - 125 - PopupGeometry.EdgeOffset, location.Y);
        Assert.True(workArea.Contains(new Rectangle(location, TwoRowPopup)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Place_SideTaskbarCursorNearTop_StaysBelowTheWorkingAreaTop(bool leftBar)
    {
        var workArea = leftBar ? new Rectangle(48, 0, 1872, 1080) : new Rectangle(0, 0, 1872, 1080);
        var cursor = new Point(leftBar ? 20 : 1900, 20);

        var location = PopupGeometry.Place(Bounds, workArea, cursor, TwoRowPopup);

        Assert.Equal(PopupGeometry.EdgeOffset, location.Y);
        Assert.Equal(leftBar ? 48 + PopupGeometry.EdgeOffset : 1872 - 272 - PopupGeometry.EdgeOffset, location.X);
    }

    [Fact]
    public void Place_BottomTaskbarCursorNearRightCorner_StaysInsideTheWorkingArea()
    {
        var workArea = new Rectangle(0, 0, 1920, 1032);

        var location = PopupGeometry.Place(Bounds, workArea, new Point(1915, 1075), TwoRowPopup);

        Assert.Equal(new Point(1920 - 272 - PopupGeometry.EdgeOffset, 1032 - 125 - PopupGeometry.EdgeOffset), location);
    }

    [Fact]
    public void Place_OversizePopup_IsPinnedToTheWorkingAreaTopLeft()
    {
        var small = new Rectangle(0, 0, 400, 300);

        var location = PopupGeometry.Place(small, small, new Point(200, 150), new Size(500, 400));

        Assert.Equal(new Point(0, 0), location);
    }

    [Fact]
    public void FitPerRow_TwentyWideOn1366Screen_FitsWithoutChange()
    {
        var workArea = new Rectangle(0, 0, 1366, 720);

        var perRow = PopupGeometry.FitPerRow(20, 20, workArea);
        var size = PopupGeometry.SizeFor(20, perRow);
        var location = PopupGeometry.Place(new Rectangle(0, 0, 1366, 768), workArea, new Point(683, 740), size);

        Assert.Equal(20, perRow);
        Assert.Equal(new Size(888, 51), size);
        Assert.True(workArea.Contains(new Rectangle(location, size)));
    }

    [Fact]
    public void FitPerRow_TooWide_DropsColumns()
    {
        // 20 per row needs 888px; a 600px work area leaves 580 usable: (580-8)/44 = 13 columns.
        var perRow = PopupGeometry.FitPerRow(20, 20, new Rectangle(0, 0, 600, 1000));

        Assert.Equal(13, perRow);
        Assert.Equal(580, PopupGeometry.SizeFor(20, perRow).Width);
    }

    [Fact]
    public void FitPerRow_TooTall_AddsColumns()
    {
        // Width 1 with 20 entries is 887px tall; 720-20 = 700 usable fits 15 rows (667px) -> 2 columns.
        var perRow = PopupGeometry.FitPerRow(20, 1, new Rectangle(0, 0, 1366, 720));

        Assert.Equal(2, perRow);
        Assert.Equal(new Size(96, 447), PopupGeometry.SizeFor(20, perRow));
    }

    [Fact]
    public void FitPerRow_AlreadyFits_KeepsTheGroupWidth()
    {
        Assert.Equal(6, PopupGeometry.FitPerRow(12, 6, new Rectangle(0, 0, 1920, 1040)));
        Assert.Equal(20, PopupGeometry.FitPerRow(3, 99, new Rectangle(0, 0, 1920, 1040)));
    }

    // ---------------------------------------------------------------- hidden taskbar

    [Fact]
    public void Place_HiddenTaskbarClickInBottomBand_OpensAboveTheBand()
    {
        var location = PopupGeometry.Place(Bounds, Bounds, new Point(500, 1070), TwoRowPopup);

        Assert.Equal(new Point(364, 910), location);
    }

    [Fact]
    public void Place_HiddenTaskbarClickAboveBand_OpensAboveTheClickPoint()
    {
        var location = PopupGeometry.Place(Bounds, Bounds, new Point(500, 500), TwoRowPopup);

        Assert.Equal(new Point(364, 355), location);
    }

    // ---------------------------------------------------------------- multi-monitor

    [Fact]
    public void Place_CursorOnSecondaryMonitor_PlacesPopupOnThatMonitor()
    {
        var secondary = new Rectangle(1920, 0, 1920, 1080);
        var secondaryWorkArea = new Rectangle(1920, 0, 1920, 1040);

        var location = PopupGeometry.Place(secondary, secondaryWorkArea, new Point(2880, 1060), TwoRowPopup);

        Assert.Equal(new Point(2744, 905), location);
        Assert.True(secondary.Contains(new Rectangle(location, TwoRowPopup)));
    }

    [Fact]
    public void Place_SecondaryMonitorLeftOfPrimary_UsesVirtualCoordinates()
    {
        var secondary = new Rectangle(-1920, 0, 1920, 1080);
        var secondaryWorkArea = new Rectangle(-1920, 40, 1920, 1040);

        var location = PopupGeometry.Place(secondary, secondaryWorkArea, new Point(-960, 20), TwoRowPopup);

        Assert.Equal(new Point(-1096, 50), location);
    }
}
