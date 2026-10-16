using System.Drawing;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>Which taskbar was clicked and whether a hit-tested element is that click's button.</summary>
public sealed class TaskbarLocatorTests
{
    private static readonly Rectangle RevealedBar = Rectangle.FromLTRB(0, 1032, 1920, 1080);
    private static readonly Rectangle HiddenBar = Rectangle.FromLTRB(-1920, 1078, 0, 1126);

    [Fact]
    public void ChooseBar_PicksTheRevealedBarUnderTheCursor()
    {
        Assert.Equal(RevealedBar, TaskbarLocator.ChooseBar(new[] { HiddenBar, RevealedBar }, new Point(76, 1060)));
    }

    [Fact]
    public void ChooseBar_IsNullWhenTheClickWasNotOnATaskbar()
    {
        // A keyboard launch (Win+N) or a click elsewhere: keep the cursor-based placement.
        Assert.Null(TaskbarLocator.ChooseBar(new[] { HiddenBar, RevealedBar }, new Point(800, 500)));
        Assert.Null(TaskbarLocator.ChooseBar(Array.Empty<Rectangle>(), new Point(76, 1060)));
    }

    [Fact]
    public void ButtonCenter_AcceptsAButtonSizedHitAndReturnsItsCentre()
    {
        var button = new Rectangle(54, 1036, 44, 40);

        Assert.Equal(76, TaskbarLocator.ButtonCenter(button, RevealedBar, new Point(70, 1060)));
    }

    [Theory]
    [InlineData(0, 1032, 1920, 48)]   // the whole bar
    [InlineData(400, 1032, 900, 48)]  // the whole task list
    [InlineData(60, 1050, 4, 4)]      // a tiny glyph
    [InlineData(200, 1036, 44, 40)]   // a button that is not under the cursor
    [InlineData(54, 900, 44, 40)]     // something above the bar
    public void ButtonCenter_RejectsHitsThatAreNotTheClickedButton(int x, int y, int width, int height)
    {
        Assert.Null(TaskbarLocator.ButtonCenter(new Rectangle(x, y, width, height), RevealedBar, new Point(70, 1060)));
    }

    [Fact]
    public void ButtonCenter_VerticalBarUsesTheVerticalCentre()
    {
        var bar = new Rectangle(0, 0, 48, 1080);

        Assert.Equal(522, TaskbarLocator.ButtonCenter(new Rectangle(4, 500, 40, 44), bar, new Point(20, 510)));
    }

    [Fact]
    public void Locate_OffEveryTaskbar_IsNull()
    {
        Assert.Null(TaskbarLocator.Locate(new Point(-99999, -99999)));
    }

    [Fact]
    public void ElementRectAt_TheComBindingAnswers()
    {
        // Read-only: asks UI Automation which element is at the primary screen's centre and
        // checks a sane rectangle comes back (proves the minimal COM vtable is right).
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var center = new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));

        var rect = TaskbarLocator.ElementRectAt(center);

        Assert.NotNull(rect);
        Assert.True(rect!.Value.Width > 0 && rect.Value.Height > 0);
        Assert.True(Rectangle.Inflate(rect.Value, 1, 1).Contains(center), $"{rect} should contain {center}");
    }
}
