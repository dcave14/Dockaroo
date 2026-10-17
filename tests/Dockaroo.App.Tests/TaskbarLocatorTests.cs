using System.Drawing;
using System.Windows.Forms;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Which taskbar was clicked, whether a hit-tested element is that click's button, and the
/// cursor-independent group button match the popup falls back to when the mouse swiped away
/// mid-handoff (popup-anchor fix, 2026-10-17). The matcher tests drive fake bars, buttons and
/// screens through the seam - no test here touches live UI Automation except the one COM-binding
/// check.
/// </summary>
public sealed class TaskbarLocatorTests
{
    private const string PcStuffAutomationId = "Appid: Dockaroo.menu.PC_STUFF";

    private static readonly Rectangle RevealedBar = Rectangle.FromLTRB(0, 1032, 1920, 1080);
    private static readonly Rectangle HiddenBar = Rectangle.FromLTRB(-1920, 1078, 0, 1126);
    private static readonly Rectangle SecondaryRevealedBar = Rectangle.FromLTRB(-1920, 1032, 0, 1080);
    private static readonly Rectangle PrimaryScreenBounds = new(0, 0, 1920, 1080);
    private static readonly Rectangle SecondaryScreenBounds = new(-1920, 0, 1920, 1080);
    private static readonly IntPtr PrimaryBarWindow = new(0x1111);
    private static readonly IntPtr SecondaryBarWindow = new(0x2222);

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

    // --- The group button matcher (probe key: "Appid: " + the .lnk AppUserModelID) ---

    [Fact]
    public void ExpectedAutomationId_IsAppidPlusTheLnkAppUserModelId()
    {
        // GroupLnkWriter turns the space into an underscore: the probe-confirmed button id.
        Assert.Equal("Appid: Dockaroo.menu.PC_STUFF", TaskbarLocator.ExpectedAutomationId("PC STUFF"));
    }

    [Theory]
    [InlineData("Appid: Dockaroo.menu.PC_STUFF")]
    [InlineData("  appid: dockaroo.menu.pc_stuff  ")]
    [InlineData("APPID: DOCKAROO.MENU.PC_STUFF")]
    public void MatchesGroup_IsTrimmedAndCaseInsensitive(string automationId)
    {
        Assert.True(TaskbarLocator.MatchesGroup(automationId, "PC STUFF"));
    }

    [Theory]
    [InlineData("Appid: Dockaroo.menu.PC")]
    [InlineData("Appid: Dockaroo.menu.PC_STUFF_2")]
    [InlineData("Dockaroo.menu.PC_STUFF")]
    [InlineData("")]
    [InlineData(null)]
    public void MatchesGroup_RejectsOtherIds(string? automationId)
    {
        Assert.False(TaskbarLocator.MatchesGroup(automationId, "PC STUFF"));
    }

    [Fact]
    public void MatchesGroup_RejectsAnInvalidGroupNameWithoutThrowing()
    {
        Assert.Null(TaskbarLocator.ExpectedAutomationId(" PC STUFF"));
        Assert.Null(TaskbarLocator.ExpectedAutomationId(null));
        Assert.False(TaskbarLocator.MatchesGroup(PcStuffAutomationId, " PC STUFF"));
    }

    // --- The cursor-independent anchor (fake bars + fake buttons + fake screens via the seam) ---

    [Fact]
    public void AnchorForGroup_TestA_MidScreenCursorPlacesOnTheMatchedButton()
    {
        // Test A: the captured cursor is mid-screen (the swipe already happened), the matching
        // button is on a revealed bar. The button must win, and the placement must be
        // PlaceOnTaskbar(buttonRect) - the rect asserted exactly, not just "not the cursor".
        var capturedCursor = new Point(960, 500);
        var uia = new FakeTaskbarUia(PrimaryScreenBounds).Add(
            new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
            new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1032, 44, 48)));

        var anchor = TaskbarLocator.AnchorForGroup(capturedCursor, "PC STUFF", uia);

        Assert.NotNull(anchor);
        Assert.True(anchor!.ButtonFound);
        Assert.Equal(RevealedBar, anchor.Bar);
        Assert.Equal(919, anchor.ButtonCenter);
        Assert.Equal(PrimaryBarWindow, anchor.BarWindow);

        var popupSize = new Size(140, 51);
        var location = PopupGeometry.PlaceOnTaskbar(PrimaryScreenBounds, anchor.Bar, anchor.ButtonCenter, popupSize);
        Assert.Equal(new Rectangle(919 - 70, 1032 - 51 + 2, 140, 51), new Rectangle(location, popupSize));
    }

    [Fact]
    public void AnchorForGroup_MatchesCaseAndWhitespaceInsensitively()
    {
        var uia = new FakeTaskbarUia(PrimaryScreenBounds).Add(
            new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
            new TaskbarButton("  APPID: dockaroo.MENU.pc_stuff ", new Rectangle(897, 1032, 44, 48)));

        var anchor = TaskbarLocator.AnchorForGroup(new Point(960, 500), "PC STUFF", uia);

        Assert.NotNull(anchor);
        Assert.Equal(919, anchor!.ButtonCenter);
    }

    [Fact]
    public void AnchorForGroup_IsNullWhenNoBarCarriesTheGroupsButton()
    {
        // An Explorer-launched unpinned .lnk: other groups' buttons exist, none matches.
        var uia = new FakeTaskbarUia(PrimaryScreenBounds).Add(
            new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
            new TaskbarButton("Appid: Dockaroo.menu.Browsers", new Rectangle(721, 1032, 44, 48)));

        Assert.Null(TaskbarLocator.AnchorForGroup(new Point(960, 500), "PC STUFF", uia));
        Assert.Null(TaskbarLocator.AnchorForGroup(new Point(960, 500), " PC STUFF", uia));
    }

    [Fact]
    public void AnchorForGroup_WithTwoBarsPrefersTheBarContainingTheCapturedCursorX()
    {
        // Both bars carry the group. A vertical swipe keeps the captured X on the clicked bar's
        // monitor, so that bar wins even though the other is the primary one.
        var uia = new FakeTaskbarUia(PrimaryScreenBounds, SecondaryScreenBounds)
            .Add(
                new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1032, 44, 48)))
            .Add(
                new TaskbarBar(SecondaryBarWindow, SecondaryRevealedBar, IsPrimary: false),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(-1023, 1032, 44, 48)));

        var anchor = TaskbarLocator.AnchorForGroup(new Point(-500, 500), "PC STUFF", uia);

        Assert.NotNull(anchor);
        Assert.Equal(SecondaryRevealedBar, anchor!.Bar);
        Assert.Equal(SecondaryBarWindow, anchor.BarWindow);
        Assert.Equal(-1001, anchor.ButtonCenter); // -1023 + 44/2
    }

    [Fact]
    public void AnchorForGroup_WithoutAContainingBarFallsBackToPrimaryThenFirstFound()
    {
        var secondaryFirst = new FakeTaskbarUia(PrimaryScreenBounds, SecondaryScreenBounds)
            .Add(
                new TaskbarBar(SecondaryBarWindow, SecondaryRevealedBar, IsPrimary: false),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(-1023, 1032, 44, 48)))
            .Add(
                new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1032, 44, 48)));

        // Captured X in no bar's range: the primary bar wins despite being listed last.
        var onPrimary = TaskbarLocator.AnchorForGroup(new Point(5000, 500), "PC STUFF", secondaryFirst);
        Assert.NotNull(onPrimary);
        Assert.Equal(PrimaryBarWindow, onPrimary!.BarWindow);

        // No primary bar either: the first found wins.
        var firstFound = new FakeTaskbarUia(PrimaryScreenBounds, SecondaryScreenBounds)
            .Add(
                new TaskbarBar(SecondaryBarWindow, SecondaryRevealedBar, IsPrimary: false),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(-1023, 1032, 44, 48)))
            .Add(
                new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: false),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1032, 44, 48)));

        var onFirst = TaskbarLocator.AnchorForGroup(new Point(5000, 500), "PC STUFF", firstFound);
        Assert.NotNull(onFirst);
        Assert.Equal(SecondaryBarWindow, onFirst!.BarWindow);
    }

    [Fact]
    public void AnchorForGroup_SnapsAParkedBarAndItsButtonBeforeAnchoring()
    {
        // The parked bar reports 1078-1126 (46px below the revealed rect); the anchor must use
        // the revealed rects so the popup opens tucked under where the bar actually appears.
        var parkedBar = Rectangle.FromLTRB(0, 1078, 1920, 1126);
        var uia = new FakeTaskbarUia(PrimaryScreenBounds).Add(
            new TaskbarBar(PrimaryBarWindow, parkedBar, IsPrimary: true),
            new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1078, 44, 48)));

        var anchor = TaskbarLocator.AnchorForGroup(new Point(960, 500), "PC STUFF", uia);

        Assert.NotNull(anchor);
        Assert.Equal(RevealedBar, anchor!.Bar);
        Assert.Equal(919, anchor.ButtonCenter);
    }

    [Fact]
    public void AnchorForGroup_TestE_StackedMonitorsResolveTheParkedBarToItsOwnScreen()
    {
        // Test E: a monitor directly below the primary. The parked primary bar's 46px overhang
        // reaches into the lower monitor, but its visible 2px sliver is on the primary - the
        // smallest non-zero intersection. The resolution must pick the primary, snap there, and
        // the popup must end up fully inside the primary screen.
        var primary = new Rectangle(0, 0, 1920, 1080);
        var below = new Rectangle(0, 1080, 1920, 1080);
        var parkedBar = Rectangle.FromLTRB(0, 1078, 1920, 1126);
        var uia = new FakeTaskbarUia(primary, below).Add(
            new TaskbarBar(PrimaryBarWindow, parkedBar, IsPrimary: true),
            new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1078, 44, 48)));

        var anchor = TaskbarLocator.AnchorForGroup(new Point(960, 500), "PC STUFF", uia);

        Assert.NotNull(anchor);
        Assert.Equal(Rectangle.FromLTRB(0, 1032, 1920, 1080), anchor!.Bar);
        Assert.Equal(919, anchor.ButtonCenter);

        var popupSize = new Size(140, 51);
        var popup = new Rectangle(PopupGeometry.PlaceOnTaskbar(primary, anchor.Bar, anchor.ButtonCenter, popupSize), popupSize);
        Assert.True(primary.Contains(popup), $"{popup} must be inside the primary screen {primary}");
        Assert.False(below.Contains(popup), $"{popup} must not fall into the stacked screen below");
    }

    // --- F-027: a matched bar beats the captured cursor's screen (fake monitors) ---

    [Fact]
    public void MatchedButtonOnAnotherMonitor_IsPlacedOnTheBarsScreen()
    {
        // F-027: a fast cross-monitor swipe captures the cursor on monitor 2 after the button on
        // monitor 1 was matched. The bar's screen must decide: the final rect is exactly
        // PlaceOnTaskbar(monitor 1) - the old cursor-screen guard dropped the matched bar.
        var monitor1 = new Rectangle(0, 0, 1920, 1080);
        var monitor2 = new Rectangle(-1920, 0, 1920, 1080);
        var barA = Rectangle.FromLTRB(0, 1032, 1920, 1080);
        var cursor = new Point(-500, 500); // captured on monitor 2, after the swipe
        var cursorWorkingArea = new Rectangle(-1920, 0, 1920, 1032);
        var popupSize = new Size(140, 51);
        var taskbar = new TaskbarAnchor(barA, 919, ButtonFound: true) { BarWindow = PrimaryBarWindow };
        var screens = new[] { monitor1, monitor2 };

        var (location, barScreen) = PopupForm.PlaceForAnchor(
            cursor, monitor2, cursorWorkingArea, popupSize, PopupMetrics.Default, taskbar, screens);

        var expected = PopupGeometry.PlaceOnTaskbar(monitor1, barA, taskbar.ButtonCenter, popupSize);
        Assert.Equal(monitor1, barScreen);
        Assert.Equal(new Rectangle(expected, popupSize), new Rectangle(location, popupSize));

        // The dropped-button defect would have placed the popup above the captured cursor instead.
        var cursorPlacement = PopupGeometry.Place(monitor2, cursorWorkingArea, cursor, popupSize);
        Assert.NotEqual(new Rectangle(cursorPlacement, popupSize), new Rectangle(location, popupSize));
    }

    [Fact]
    public void WithoutAMatchedBar_TheCapturedCursorStillPlacesThePopup()
    {
        // No taskbar anchor at all (an Explorer-launched unpinned .lnk): the captured cursor
        // stays the anchor and its own screen decides - the F-027 fallback, unchanged.
        var monitor1 = new Rectangle(0, 0, 1920, 1080);
        var monitor2 = new Rectangle(-1920, 0, 1920, 1080);
        var cursor = new Point(-500, 500);
        var cursorWorkingArea = new Rectangle(-1920, 0, 1920, 1032);
        var popupSize = new Size(140, 51);
        var screens = new[] { monitor1, monitor2 };

        var (location, barScreen) = PopupForm.PlaceForAnchor(
            cursor, monitor2, cursorWorkingArea, popupSize, PopupMetrics.Default, taskbar: null, screens: screens);

        Assert.Equal(Rectangle.Empty, barScreen);
        Assert.Equal(
            PopupGeometry.Place(monitor2, cursorWorkingArea, cursor, popupSize, PopupMetrics.Default),
            location);
    }

    [Fact]
    public void WithGroupAnchor_WithoutACapturedCursorStillMatchesTheButton()
    {
        // A cursor-less request (legacy payload): the match is cursor-independent - only the
        // bar tie-break needs the cursor - so the button still wins.
        var request = new PopupRequest(Point.Empty, "PC STUFF", HasCursor: false);
        var uia = new FakeTaskbarUia(PrimaryScreenBounds).Add(
            new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
            new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1032, 44, 48)));

        var anchored = TrayContext.WithGroupAnchor(request, uia);

        Assert.NotNull(anchored.Taskbar);
        Assert.True(anchored.Taskbar!.ButtonFound);
        Assert.Equal(RevealedBar, anchored.Taskbar.Bar);
        Assert.Equal(919, anchored.Taskbar.ButtonCenter);
        Assert.False(anchored.HasCursor);
    }

    [Fact]
    public void WithGroupAnchor_WithoutACursorTheTieBreakSkipsTheXHint()
    {
        // A stacked layout: both bars' X ranges would contain a nonexistent cursor X of 0. With
        // no captured cursor the X hint is meaningless, so the primary bar wins even though the
        // lower monitor's bar is listed first.
        var primary = new Rectangle(0, 0, 1920, 1080);
        var below = new Rectangle(0, 1080, 1920, 1080);
        var uia = new FakeTaskbarUia(primary, below)
            .Add(
                new TaskbarBar(SecondaryBarWindow, Rectangle.FromLTRB(0, 2112, 1920, 2160), IsPrimary: false),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 2112, 44, 48)))
            .Add(
                new TaskbarBar(PrimaryBarWindow, RevealedBar, IsPrimary: true),
                new TaskbarButton(PcStuffAutomationId, new Rectangle(897, 1032, 44, 48)));

        var anchored = TrayContext.WithGroupAnchor(new PopupRequest(Point.Empty, "PC STUFF", HasCursor: false), uia);

        Assert.NotNull(anchored.Taskbar);
        Assert.Equal(PrimaryBarWindow, anchored.Taskbar!.BarWindow);
        Assert.Equal(RevealedBar, anchored.Taskbar.Bar);
    }

    [Fact]
    public void ElementRectAt_TheComBindingAnswers()
    {
        // Read-only: asks UI Automation for the element at a point inside a window this test
        // owns. The probe sits off-screen at (-32000,-32000) per TESTING.md (never on the
        // visible desktop), is shown without activation and closed right after the call; UIA
        // ElementFromPoint answers for negative coordinates.
        var ready = new ManualResetEventSlim(false);
        Form? probe = null;
        var probePoint = Point.Empty;
        var thread = new Thread(() =>
        {
            var form = new ProbeWindow { Location = new Point(-32000, -32000) };

            form.Show();
            probe = form;
            probePoint = new Point(form.Left + (form.Width / 2), form.Top + (form.Height / 2));
            ready.Set();
            Application.Run(form);
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "the probe window did not show");

            var rect = TaskbarLocator.ElementRectAt(probePoint);

            Assert.NotNull(rect);
            Assert.True(rect!.Value.Width > 0 && rect.Value.Height > 0);
            Assert.True(Rectangle.Inflate(rect.Value, 1, 1).Contains(probePoint), $"{rect} should contain {probePoint}");
        }
        finally
        {
            try
            {
                var window = probe;
                if (window is { IsDisposed: false })
                {
                    window.BeginInvoke(new Action(window.Close));
                }
            }
            catch (InvalidOperationException)
            {
                // The probe thread already went away; nothing left to close.
            }

            thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Tiny probe window, shown without activation so the hit test lands on it.</summary>
    private sealed class ProbeWindow : Form
    {
        public ProbeWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(8, 8);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>In-memory bars, buttons and screens; no test here touches live UIA.</summary>
    private sealed class FakeTaskbarUia : ITaskbarUia
    {
        private readonly List<Rectangle> _screens;
        private readonly List<TaskbarBar> _bars = new();
        private readonly List<List<TaskbarButton>> _buttons = new();

        public FakeTaskbarUia(params Rectangle[] screens)
        {
            _screens = screens.ToList();
        }

        public FakeTaskbarUia Add(TaskbarBar bar, params TaskbarButton[] buttons)
        {
            _bars.Add(bar);
            _buttons.Add(buttons.ToList());
            return this;
        }

        public IReadOnlyList<TaskbarBar> Bars() => _bars;

        public IReadOnlyList<TaskbarButton> Buttons(TaskbarBar bar) => _buttons[_bars.IndexOf(bar)];

        public IReadOnlyList<Rectangle> Screens() => _screens;
    }
}
