using System.Drawing;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.Core.Tests;

/// <summary>
/// User rulings 2026-10-15: a popup opened from a taskbar button is always centred on that
/// button and sits on the taskbar with its bar-side edge tucked 2px (at 100%) under the bar, so
/// the bar covers the popup's trim and the trims do not double up.
/// </summary>
public sealed class PopupTaskbarPlacementTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);
    private static readonly Size ThreeApps = new(140, 51);

    [Fact]
    public void AutoHideBottomBar_TheUsersSetup_IsFlushAndCentred()
    {
        // Measured: auto-hide bar revealed at 0,1032-1920,1080; Browsers button centred at x=76.
        var bar = Rectangle.FromLTRB(0, 1032, 1920, 1080);

        var location = PopupGeometry.PlaceOnTaskbar(Screen, bar, 76, ThreeApps);

        Assert.Equal(new Point(76 - 70, 1032 - 51 + 2), location);
        Assert.Equal(bar.Top + 2, location.Y + ThreeApps.Height); // tucked: trim + 1px behind the bar
        Assert.Equal(4, bar.Top - (location.Y + ThreeApps.Height - PopupGeometry.BottomPad)); // visible margin below the plates
        Assert.Equal(76, location.X + (ThreeApps.Width / 2)); // centred on the button
    }

    [Fact]
    public void TopBar_SitsDirectlyBelowIt()
    {
        var bar = new Rectangle(0, 0, 1920, 48);

        var location = PopupGeometry.PlaceOnTaskbar(Screen, bar, 960, ThreeApps);

        Assert.Equal(new Point(890, 48 - 2), location);
    }

    [Fact]
    public void LeftAndRightBars_AreFlushAndCentredVertically()
    {
        var left = new Rectangle(0, 0, 48, 1080);
        var right = new Rectangle(1872, 0, 48, 1080);

        Assert.Equal(new Point(48 - 2, 500 - 25), PopupGeometry.PlaceOnTaskbar(Screen, left, 500, ThreeApps));
        Assert.Equal(new Point(1872 - 140 + 2, 500 - 25), PopupGeometry.PlaceOnTaskbar(Screen, right, 500, ThreeApps));
    }

    [Theory]
    [InlineData(20, 0)]
    [InlineData(1910, 1920 - 140)]
    public void NearAScreenEdge_OnlyMovesAsFarAsNeededAndStaysFlush(int buttonCenter, int expectedX)
    {
        var bar = Rectangle.FromLTRB(0, 1032, 1920, 1080);

        var location = PopupGeometry.PlaceOnTaskbar(Screen, bar, buttonCenter, ThreeApps);

        Assert.Equal(expectedX, location.X);
        Assert.Equal(1032 - 51 + 2, location.Y);
    }

    [Fact]
    public void SecondaryMonitorLeftOfPrimary_UsesItsOwnBar()
    {
        var screen = new Rectangle(-1920, 0, 1920, 1080);
        var bar = Rectangle.FromLTRB(-1920, 1032, 0, 1080);

        var location = PopupGeometry.PlaceOnTaskbar(screen, bar, -1000, ThreeApps);

        Assert.Equal(new Point(-1070, 983), location);
    }

    [Fact]
    public void Tuck_ScalesWithDpi()
    {
        var bar = Rectangle.FromLTRB(0, 1548, 2880, 1620); // 150%: 72px bar
        var metrics = PopupMetrics.ForDpi(144);
        var size = PopupGeometry.SizeFor(3, 6, metrics);

        var location = PopupGeometry.PlaceOnTaskbar(new Rectangle(0, 0, 2880, 1620), bar, 1440, size, metrics);

        Assert.Equal(3, metrics.TaskbarTuck);
        Assert.Equal(bar.Top + 3, location.Y + size.Height);
    }

    [Theory]
    [InlineData(0, 1032, 1920, 1080, TaskbarEdge.Bottom)]
    [InlineData(0, 0, 1920, 48, TaskbarEdge.Top)]
    [InlineData(0, 0, 48, 1080, TaskbarEdge.Left)]
    [InlineData(1872, 0, 1920, 1080, TaskbarEdge.Right)]
    public void EdgeOfBar_FollowsShapeAndPosition(int left, int top, int right, int bottom, TaskbarEdge edge)
    {
        Assert.Equal(edge, PopupGeometry.EdgeOfBar(Screen, Rectangle.FromLTRB(left, top, right, bottom)));
    }

    [Fact]
    public void BarCoversTuck_WhileTheBarOverlapsThePopup_NotOnceItHasSlidAway()
    {
        var bar = Rectangle.FromLTRB(0, 1032, 1920, 1080);
        var popup = new Rectangle(890, 1032 - 51 + 2, 140, 51); // tucked 2px under the bar

        Assert.True(PopupGeometry.BarCoversTuck(popup, bar));
        Assert.False(PopupGeometry.BarCoversTuck(popup, Rectangle.FromLTRB(0, 1078, 1920, 1126))); // auto-hide sliver
        Assert.False(PopupGeometry.BarCoversTuck(popup, Rectangle.Empty));
    }
}
