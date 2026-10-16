using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Popup keyboard and launch-failure handling. The popup handle is created but never shown, key
/// events are delivered by calling the protected handlers directly (no input injection), and
/// every shortcut points at a missing file so nothing is ever started: a launch attempt shows up
/// as a LaunchFailed report instead.
/// </summary>
public sealed class PopupInputTests
{
    [Fact]
    public void KeyState_KeyUpWithoutKeyDown_IsIgnored()
    {
        var keys = new PopupKeyState();

        Assert.False(keys.Release(Keys.D1));
    }

    [Fact]
    public void KeyState_KeyDownThenKeyUp_IsAcceptedOnce()
    {
        var keys = new PopupKeyState();

        Assert.True(keys.Press(Keys.D1));
        Assert.False(keys.Press(Keys.D1)); // auto-repeat
        Assert.True(keys.Release(Keys.D1));
        Assert.False(keys.Release(Keys.D1));
        Assert.False(keys.Release(Keys.D2));
    }

    [Fact]
    public void KeyState_HeldKeyArrivingAsRepeatsOnly_IsNeverRecorded()
    {
        // Win+1 held past the repeat delay: the popup sees repeat KeyDowns (bit 30 set), then KeyUp.
        var keys = new PopupKeyState();

        Assert.False(keys.Press(Keys.D1, wasAlreadyDown: true));
        Assert.False(keys.Press(Keys.D1, wasAlreadyDown: true));
        Assert.False(keys.Release(Keys.D1));
    }

    [Fact]
    public void KeyState_RepeatAfterARealPress_StaysRecorded()
    {
        var keys = new PopupKeyState();

        Assert.True(keys.Press(Keys.D3));
        Assert.False(keys.Press(Keys.D3, wasAlreadyDown: true));
        Assert.True(keys.Release(Keys.D3));
    }

    [Fact]
    public void Popup_StrayDigitKeyUp_DoesNotLaunch()
    {
        // Win+1 opens the group pinned in slot 1; the popup then sees only the KeyUp of 1.
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(MissingFilesGroup(3, allowOpenAll: false), icons, new Point(10, 10));
            _ = popup.Handle;
            var reports = new List<IReadOnlyList<string>>();
            popup.LaunchFailed += reports.Add;

            Invoke(popup, "OnKeyUp", new KeyEventArgs(Keys.D1));

            Assert.Empty(reports);
            Assert.False(popup.IsDisposed);
        });
    }

    [Fact]
    public void Popup_DigitPressedInThePopup_LaunchesThatEntry()
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(MissingFilesGroup(3, allowOpenAll: false), icons, new Point(10, 10));
            _ = popup.Handle;
            var reports = new List<IReadOnlyList<string>>();
            popup.LaunchFailed += reports.Add;

            Invoke(popup, "OnKeyDown", new KeyEventArgs(Keys.D2));
            Invoke(popup, "OnKeyUp", new KeyEventArgs(Keys.D2));

            var report = Assert.Single(reports);
            var line = Assert.Single(report);
            Assert.StartsWith("Missing 2: ", line, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Popup_LaunchAllWithEveryEntryMissing_RaisesOneAggregatedReportAndCloses()
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(MissingFilesGroup(3, allowOpenAll: true), icons, new Point(10, 10));
            _ = popup.Handle;
            var closing = false;
            popup.FormClosing += (_, _) => closing = true;
            var reports = new List<IReadOnlyList<string>>();
            popup.LaunchFailed += reports.Add;

            // Ctrl+Enter acts on KeyDown (P2-5), so the release order of the keys is irrelevant.
            Invoke(popup, "OnKeyDown", new KeyEventArgs(Keys.Enter | Keys.Control));

            var report = Assert.Single(reports);
            Assert.Equal(3, report.Count);
            Assert.StartsWith("Missing 1: ", report[0], StringComparison.Ordinal);
            Assert.StartsWith("Missing 2: ", report[1], StringComparison.Ordinal);
            Assert.StartsWith("Missing 3: ", report[2], StringComparison.Ordinal);
            Assert.True(closing, "a failed launch closes the popup before reporting");
        });
    }

    [Fact]
    public void Popup_CtrlEnterAutoRepeat_LaunchesAllOnlyOnce()
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(MissingFilesGroup(2, allowOpenAll: true), icons, new Point(10, 10));
            _ = popup.Handle;
            var reports = new List<IReadOnlyList<string>>();
            popup.LaunchFailed += reports.Add;

            Invoke(popup, "OnKeyDown", new KeyEventArgs(Keys.Enter | Keys.Control));
            Invoke(popup, "OnKeyDown", new KeyEventArgs(Keys.Enter | Keys.Control));

            Assert.Single(reports);
        });
    }

    [Fact]
    public void Popup_CtrlEnterWithoutAllowOpenAll_LaunchesNothing()
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(TempDirectory());
            using var popup = new PopupForm(MissingFilesGroup(2, allowOpenAll: false), icons, new Point(10, 10));
            _ = popup.Handle;
            var reports = new List<IReadOnlyList<string>>();
            popup.LaunchFailed += reports.Add;

            Invoke(popup, "OnKeyDown", new KeyEventArgs(Keys.Enter | Keys.Control));
            Invoke(popup, "OnKeyUp", new KeyEventArgs(Keys.Enter | Keys.Control));

            Assert.Empty(reports);
        });
    }

    [Fact]
    public void TrayMessages_LaunchFailures_SingleAndAggregated()
    {
        Assert.Equal("Could not start A: gone", TrayMessages.LaunchFailures(new[] { "A: gone" }));

        var text = TrayMessages.LaunchFailures(new[] { "A: gone", "B: denied" });
        Assert.Equal($"2 shortcuts could not be started:{Environment.NewLine}A: gone{Environment.NewLine}B: denied", text);
    }

    [Fact]
    public void TrayMessages_LaunchFailures_LongTextIsCutToTheBalloonLimit()
    {
        var failures = Enumerable.Range(1, 20).Select(i => $"Entry {i}: The system cannot find the file specified.").ToArray();

        var text = TrayMessages.LaunchFailures(failures);

        Assert.Equal(TrayMessages.MaxBalloonText, text.Length);
        Assert.StartsWith("20 shortcuts could not be started:", text, StringComparison.Ordinal);
        Assert.EndsWith("...", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayMessages_ForFailedLoad_NamesTheGroup()
    {
        Assert.Equal(
            "Group 'Old Name' no longer exists. If it was renamed, re-pin its taskbar button.",
            TrayMessages.ForFailedLoad("Old Name", GroupLoadStatus.NotFound));
        Assert.Contains("'Broken' could not be read", TrayMessages.ForFailedLoad("Broken", GroupLoadStatus.Corrupt), StringComparison.Ordinal);
        Assert.Null(TrayMessages.ForFailedLoad("Fine", GroupLoadStatus.Loaded));
    }

    private static Group MissingFilesGroup(int count, bool allowOpenAll)
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", "missing-" + Guid.NewGuid().ToString("N"));
        var group = new Group { Name = "Input Test", Width = 6, AllowOpenAll = allowOpenAll };
        for (var i = 1; i <= count; i++)
        {
            group.Shortcuts.Add(new ShortcutEntry
            {
                TargetPath = Path.Combine(missingDirectory, $"missing{i}.exe"),
                Name = $"Missing {i}",
            });
        }

        return group;
    }

    private static void Invoke(PopupForm popup, string methodName, KeyEventArgs args)
    {
        var method = typeof(PopupForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(KeyEventArgs) });
        Assert.NotNull(method);
        method!.Invoke(popup, new object[] { args });
    }

    private static string TempDirectory()
    {
        // IconCache is lazy: nothing is written until an icon is loaded.
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
