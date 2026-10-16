using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Hidden-window content tests for the selected-state pin: the popup handle styles and window
/// AppUserModelID. No window is ever shown (the handle is created directly on an STA thread),
/// so no desktop interaction happens.
/// </summary>
public sealed class PopupWindowPinTests
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    [Fact]
    public void PinnedPopup_IsAnAppWindowCarryingTheGroupAumid()
    {
        RunOnStaThread(() =>
        {
            var appUserModelId = GroupLnkWriter.DeriveAppUserModelId("Pin Test");
            var group = new Group { Name = "Pin Test" };
            using var icons = new IconCache(TempCacheDirectory());

            using var popup = new PopupForm(group, icons, new Point(10, 10), appUserModelId);

            _ = popup.Handle; // force handle creation without showing the window
            var exStyle = GetWindowLongW(popup.Handle, GwlExStyle);
            Assert.Equal(WsExAppWindow, exStyle & WsExAppWindow);
            Assert.Equal(0, exStyle & WsExToolWindow);

            // The handle carries the pin's AppUserModelID, so the taskbar merges it.
            Assert.True(PopupForm.TryGetWindowAppUserModelId(popup.Handle, out var readBack));
            Assert.Equal(appUserModelId, readBack);
        });
    }

    [Fact]
    public void UnpinnedPopup_KeepsTheToolWindowStyle()
    {
        RunOnStaThread(() =>
        {
            var group = new Group { Name = "Pin Test" };
            using var icons = new IconCache(TempCacheDirectory());

            using var popup = new PopupForm(group, icons, new Point(10, 10));

            _ = popup.Handle;
            var exStyle = GetWindowLongW(popup.Handle, GwlExStyle);
            Assert.Equal(WsExToolWindow, exStyle & WsExToolWindow);
            Assert.Equal(0, exStyle & WsExAppWindow);
        });
    }

    [Fact]
    public void Popup_DoesNotCloseOnMouseLeave()
    {
        RunOnStaThread(() =>
        {
            var group = new Group { Name = "Pin Test" };
            using var icons = new IconCache(TempCacheDirectory());
            using var popup = new PopupForm(group, icons, new Point(10, 10));
            _ = popup.Handle;
            var closing = false;
            popup.FormClosing += (_, _) => closing = true;

            // The legacy popup only clears its hover highlight when the mouse leaves.
            InvokeOnMouseLeave(popup);

            Assert.False(closing, "mouse-leave must not close the popup");
            Assert.False(popup.IsDisposed);
        });
    }

    private static void InvokeOnMouseLeave(PopupForm popup)
    {
        var method = typeof(PopupForm).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(popup, new object[] { EventArgs.Empty });
    }

    private static string TempCacheDirectory()
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
                failure = ex;
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
}
