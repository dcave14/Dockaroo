using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// The popup lays itself out in physical pixels for its monitor's DPI (backlog P1-5). Metrics
/// are injected so 150%/200% render on a 100% desktop; the window is never shown, only its
/// handle is created and painted into a bitmap.
/// </summary>
public sealed class PopupDpiTests
{
    [Fact]
    public void PopupAt100Percent_KeepsTheMeasuredTaskbarSize()
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(ThreeAppGroup(), icons, new Point(10, 10), metrics: PopupMetrics.Default);

            Assert.Equal(new Size(140, 51), popup.ClientSize);
        });
    }

    [Theory]
    [InlineData(144, 210, 77)]
    [InlineData(192, 280, 102)]
    public void PopupAtHigherDpi_IsSizedInPhysicalPixels(int dpi, int width, int height)
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(ThreeAppGroup(), icons, new Point(10, 10), metrics: PopupMetrics.ForDpi(dpi));
            _ = popup.Handle;

            Assert.Equal(new Size(width, height), popup.ClientSize);
        });
    }

    [Fact]
    public void PopupAt150Percent_PaintsTheHoverPlateAtTheScaledPosition()
    {
        RunOnStaThread(() =>
        {
            var metrics = PopupMetrics.ForDpi(144);
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(ThreeAppGroup(), icons, new Point(10, 10), metrics: metrics);
            _ = popup.Handle;
            var plate = PopupGeometry.TileBounds(0, 6, metrics); // (9, 8, 60, 60)
            InvokeMouseMove(popup, new Point(plate.Left + 30, plate.Top + 30));

            using var bitmap = new Bitmap(popup.ClientSize.Width, popup.ClientSize.Height);
            popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.ClientSize));

            Assert.Equal(new Rectangle(9, 8, 60, 60), plate);
            // Left strip of the hovered plate, outside the 36px icon box: the plate colour.
            Assert.Equal(TaskbarPalette.Plate.ToArgb(), bitmap.GetPixel(plate.Left + 4, plate.Top + 30).ToArgb());
            // Its 1px top edge.
            Assert.Equal(TaskbarPalette.PlateTopEdge.ToArgb(), bitmap.GetPixel(plate.Left + 30, plate.Top).ToArgb());
            // The 6px gap after it, and the second (not hovered) plate's strip: plain fill.
            Assert.Equal(TaskbarPalette.Fill.ToArgb(), bitmap.GetPixel(plate.Right + 3, plate.Top + 30).ToArgb());
            Assert.Equal(TaskbarPalette.Fill.ToArgb(), bitmap.GetPixel(plate.Right + metrics.PlateGap + 4, plate.Top + 30).ToArgb());
            // The 1px trim stays a hairline at every scale.
            Assert.Equal(TaskbarPalette.Trim.ToArgb(), bitmap.GetPixel(0, 30).ToArgb());
            Assert.Equal(TaskbarPalette.Fill.ToArgb(), bitmap.GetPixel(1, 30).ToArgb());
        });
    }

    [Fact]
    public void PopupAt150Percent_DrawsThe100PercentIconScaledWhenTheScaledSizeIsNotCached()
    {
        RunOnStaThread(() =>
        {
            var metrics = PopupMetrics.ForDpi(144);
            var group = ThreeAppGroup();

            // Only the 24px icon is cached (red); the 36px lookup misses and must fall back.
            using var icons = new IconCache(TempDirectory(), (_, size) => SolidIcon(size, Color.Red));
            icons.GetOrLoad(IconSource.FromPath(group.Shortcuts[0].TargetPath), PopupMetrics.Default.IconSize);

            using var popup = new PopupForm(group, icons, new Point(10, 10), metrics: metrics);
            _ = popup.Handle;
            using var bitmap = new Bitmap(popup.ClientSize.Width, popup.ClientSize.Height);
            popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.ClientSize));

            // Icon box of tile 0: plate (9, 8) + margin 12, 36x36. Corners and centre are red.
            Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(9 + 12 + 1, 8 + 12 + 1).ToArgb());
            Assert.Equal(Color.Red.ToArgb(), bitmap.GetPixel(9 + 12 + 34, 8 + 12 + 34).ToArgb());
            Assert.Equal(TaskbarPalette.Fill.ToArgb(), bitmap.GetPixel(9 + 12 + 37, 8 + 12 + 18).ToArgb());
        });
    }

    [Fact]
    public void MonitorDpi_IconSizesAreThe100PercentSizePlusEveryMonitorsScaledSize()
    {
        var expected = new SortedSet<int> { PopupMetrics.Default.IconSize };
        foreach (var screen in Screen.AllScreens)
        {
            var center = new Point(screen.Bounds.Left + (screen.Bounds.Width / 2), screen.Bounds.Top + (screen.Bounds.Height / 2));
            var dpi = MonitorDpi.ForPoint(center);
            Assert.InRange(dpi, PopupMetrics.BaseDpi, 5 * PopupMetrics.BaseDpi);
            expected.Add(PopupMetrics.ForDpi(dpi).IconSize);
        }

        Assert.Equal(expected, MonitorDpi.PopupIconSizes());
    }

    [Fact]
    public void TaskbarPopup_IsClippedSoTheBarCoversItsTrim()
    {
        RunOnStaThread(() =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var bar = Rectangle.FromLTRB(screen.Left, screen.Bottom - 48, screen.Right, screen.Bottom);
            var anchor = new TaskbarAnchor(bar, screen.Left + 300, ButtonFound: true);
            using var icons = new IconCache(TempDirectory());

            using var popup = new PopupForm(ThreeAppGroup(), icons, new Point(screen.Left + 300, bar.Top + 20), metrics: PopupMetrics.Default, taskbar: anchor);
            _ = popup.Handle;

            // Tucked 2px under the bar, centred on the button...
            Assert.Equal(new Rectangle(screen.Left + 300 - 70, bar.Top - 51 + 2, 140, 51), popup.Bounds);

            // ...and the 2px under the bar are not part of the window at all.
            using var graphics = popup.CreateGraphics();
            Assert.NotNull(popup.Region);
            Assert.Equal(new RectangleF(0, 0, 140, 49), popup.Region!.GetBounds(graphics));
        });
    }

    [Fact]
    public void TaskbarPopup_ShowsItsOwnTrimWhileAnAutoHideBarIsAway_AndClipsAgainWhenItReturns()
    {
        RunOnStaThread(() =>
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            var bar = Rectangle.FromLTRB(screen.Left, screen.Bottom - 48, screen.Right, screen.Bottom);
            var anchor = new TaskbarAnchor(bar, screen.Left + 300, ButtonFound: true);
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(ThreeAppGroup(), icons, new Point(screen.Left + 300, bar.Top + 20), metrics: PopupMetrics.Default, taskbar: anchor);
            _ = popup.Handle;
            var opened = popup.Bounds;
            Assert.True(popup.TuckClipped);

            // The auto-hide bar slides down to a 2px sliver: the strip is uncovered, so the whole
            // popup (its bottom trim included) is shown - and it has not moved.
            popup.UpdateTuckClip(Rectangle.FromLTRB(screen.Left, screen.Bottom - 2, screen.Right, screen.Bottom + 46));
            Assert.False(popup.TuckClipped);
            Assert.Null(popup.Region);
            Assert.Equal(opened, popup.Bounds);

            // The bar comes back up: clipped again, one trim line.
            popup.UpdateTuckClip(bar);
            Assert.True(popup.TuckClipped);
            using var graphics = popup.CreateGraphics();
            Assert.Equal(new RectangleF(0, 0, 140, 49), popup.Region!.GetBounds(graphics));
        });
    }

    [Theory]
    [InlineData(Dockaroo.Core.Popup.TaskbarEdge.Bottom, 0, 0, 140, 49)]
    [InlineData(Dockaroo.Core.Popup.TaskbarEdge.Top, 0, 2, 140, 49)]
    [InlineData(Dockaroo.Core.Popup.TaskbarEdge.Left, 2, 0, 138, 51)]
    [InlineData(Dockaroo.Core.Popup.TaskbarEdge.Right, 0, 0, 138, 51)]
    public void VisibleWhenTucked_DropsTheBarSideStrip(Dockaroo.Core.Popup.TaskbarEdge edge, int x, int y, int width, int height)
    {
        Assert.Equal(new Rectangle(x, y, width, height), PopupGeometry.VisibleWhenTucked(new Size(140, 51), edge, PopupMetrics.Default));
    }

    private static IconExtractionResult SolidIcon(int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(color);
        }

        return IconExtractionResult.Ok(bitmap);
    }

    private static Group ThreeAppGroup()
    {
        var group = new Group { Name = "Dpi Test", Width = 6 };
        for (var i = 1; i <= 3; i++)
        {
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = $@"C:\Fake\App{i}\app{i}.exe", Name = $"App {i}" });
        }

        return group;
    }

    private static void InvokeMouseMove(PopupForm popup, Point point)
    {
        var method = typeof(PopupForm).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(popup, new object[] { new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0) });
    }

    private static string TempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
