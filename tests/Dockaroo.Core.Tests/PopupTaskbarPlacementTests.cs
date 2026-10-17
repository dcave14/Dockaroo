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

    // --- Group-button anchor and parked bars (popup-anchor fix, 2026-10-17) ---

    [Theory]
    [InlineData(0, 1078, 1920, 1126, 0, 1032, 1920, 1080)]      // bottom bar parked 46px down
    [InlineData(0, -46, 1920, 2, 0, 0, 1920, 48)]                // top bar parked 46px up
    [InlineData(-46, 0, 2, 1080, 0, 0, 48, 1080)]                // left bar parked 46px out
    [InlineData(1920, 0, 1968, 1080, 1872, 0, 1920, 1080)]       // right bar parked 46px out
    public void RevealedBar_SnapsEveryParkedEdgeFlushToItsMonitorEdge(
        int left, int top, int right, int bottom,
        int expectedLeft, int expectedTop, int expectedRight, int expectedBottom)
    {
        var parked = Rectangle.FromLTRB(left, top, right, bottom);

        var revealed = PopupGeometry.RevealedBar(parked, Screen);

        Assert.Equal(Rectangle.FromLTRB(expectedLeft, expectedTop, expectedRight, expectedBottom), revealed);
    }

    [Fact]
    public void RevealedBar_LeavesARevealedBarAlone()
    {
        // A bar already flush to its monitor edge is unchanged: the snap is idempotent.
        Assert.Equal(Rectangle.FromLTRB(0, 1032, 1920, 1080), PopupGeometry.RevealedBar(Rectangle.FromLTRB(0, 1032, 1920, 1080), Screen));
        Assert.Equal(new Rectangle(0, 0, 1920, 48), PopupGeometry.RevealedBar(new Rectangle(0, 0, 1920, 48), Screen));
        Assert.Equal(Rectangle.Empty, PopupGeometry.RevealedBar(Rectangle.Empty, Screen));
    }

    [Fact]
    public void MovedWithBar_CarriesAButtonByTheBarsParkedToRevealedOffset()
    {
        var parked = Rectangle.FromLTRB(0, 1078, 1920, 1126);
        var revealed = PopupGeometry.RevealedBar(parked, Screen);

        var button = PopupGeometry.MovedWithBar(new Rectangle(897, 1078, 44, 48), parked, revealed);

        Assert.Equal(new Rectangle(897, 1032, 44, 48), button);
    }

    [Fact]
    public void ScreenForBar_PrefersContainmentThenTheSmallestNonZeroIntersection()
    {
        var primary = new Rectangle(0, 0, 1920, 1080);
        var below = new Rectangle(0, 1080, 1920, 1080);
        var screens = new[] { primary, below };

        // A revealed bar is contained by its screen.
        Assert.Equal(primary, PopupGeometry.ScreenForBar(Rectangle.FromLTRB(0, 1032, 1920, 1080), screens));

        // A parked bar shows a 2px sliver on its own screen and overhangs 46px into the screen
        // below: the smallest non-zero intersection is the sliver, so the primary wins
        // (largest-intersection would pick the screen below and break the snap).
        Assert.Equal(primary, PopupGeometry.ScreenForBar(Rectangle.FromLTRB(0, 1078, 1920, 1126), screens));

        // Nothing intersects: empty (the caller skips the bar).
        Assert.Equal(Rectangle.Empty, PopupGeometry.ScreenForBar(new Rectangle(-5000, -5000, 10, 10), screens));
    }

    [Fact]
    public void GroupButtonOnARevealedBar_TestA_IsPlacedOnThatButton()
    {
        // Test A (placement half): a button matched on a revealed bar is placed by
        // PlaceOnTaskbar(buttonRect): the popup rect is asserted exactly, tucked 2px under the
        // bar and centred on the button. The mid-screen captured cursor setup is in the App-side
        // Test A (TaskbarLocatorTests.AnchorForGroup_TestA_MidScreenCursorPlacesOnTheMatchedButton).
        var bar = Rectangle.FromLTRB(0, 1032, 1920, 1080);
        var button = new Rectangle(897, 1032, 44, 48);
        var buttonCenter = button.Left + (button.Width / 2);

        var location = PopupGeometry.PlaceOnTaskbar(Screen, bar, buttonCenter, ThreeApps);

        Assert.Equal(new Rectangle(897 + 22 - 70, 1032 - 51 + 2, 140, 51), new Rectangle(location, ThreeApps));
        Assert.Equal(bar.Top + 2, location.Y + ThreeApps.Height);
    }

    [Fact]
    public void NoButtonMatch_TestB_KeepsTheCapturedCursorPlacement()
    {
        // Test B: an unpinned .lnk has no taskbar button anywhere; the captured cursor stays the
        // anchor (case 4), so the popup opens above the click point exactly as before.
        var workingArea = Rectangle.FromLTRB(0, 0, 1920, 1032); // the bar's reserved strip
        var cursor = new Point(960, 500);

        var location = PopupGeometry.Place(Screen, workingArea, cursor, ThreeApps);

        Assert.Equal(new Point(960 - 70, 500 - 51 - PopupGeometry.CursorOffset), location);
    }

    [Fact]
    public void ParkedBar_TestD_AnchorsToTheRevealedButtonRect()
    {
        // Test D: parked, the bar reports 1078-1126 and the button 1078-1126 (button rect mostly
        // below the monitor edge). Placement must use the REVEALED rects, and the popup must sit
        // fully inside the monitor with its tuck against the revealed bar.
        var parkedBar = Rectangle.FromLTRB(0, 1078, 1920, 1126);
        var parkedButton = new Rectangle(897, 1078, 44, 48);

        var revealedBar = PopupGeometry.RevealedBar(parkedBar, Screen);
        var revealedButton = PopupGeometry.MovedWithBar(parkedButton, parkedBar, revealedBar);

        Assert.Equal(Rectangle.FromLTRB(0, 1032, 1920, 1080), revealedBar);
        Assert.Equal(new Rectangle(897, 1032, 44, 48), revealedButton);

        var location = PopupGeometry.PlaceOnTaskbar(Screen, revealedBar, revealedButton.Left + (revealedButton.Width / 2), ThreeApps);
        var popup = new Rectangle(location, ThreeApps);

        Assert.Equal(new Rectangle(849, 983, 140, 51), popup);
        Assert.True(Screen.Contains(popup), $"{popup} must be fully inside {Screen}");
        Assert.Equal(revealedBar.Top + 2, popup.Bottom); // tucked against the revealed bar, not the parked one
    }
}
