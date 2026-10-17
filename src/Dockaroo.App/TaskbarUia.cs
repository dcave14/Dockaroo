using System.Drawing;
using System.Runtime.InteropServices;

namespace Dockaroo.App;

/// <summary>
/// One taskbar window: its hwnd, its live window rect (a parked auto-hide bar reports the
/// hidden sliver, see <see cref="Dockaroo.Core.Popup.PopupGeometry.RevealedBar"/>) and whether
/// it is the primary bar (Shell_TrayWnd).
/// </summary>
internal sealed record TaskbarBar(IntPtr Window, Rectangle Rect, bool IsPrimary);

/// <summary>One taskbar button: the UI Automation AutomationId (the group matcher key) and its bounding rectangle.</summary>
internal sealed record TaskbarButton(string AutomationId, Rectangle Rect);

/// <summary>
/// The taskbar reads the group button lookup needs: the taskbar windows, the buttons reachable
/// through each bar's Windows.UI.Composition.DesktopWindowContentBridge child window, and the
/// monitor a bar sits on. <see cref="TaskbarUia.Windows"/> is the production implementation;
/// tests inject fakes so no test touches live UI Automation.
/// </summary>
internal interface ITaskbarUia
{
    /// <summary>Taskbar windows in probe order: Shell_TrayWnd (primary) first, then Shell_SecondaryTrayWnd.</summary>
    IReadOnlyList<TaskbarBar> Bars();

    /// <summary>Buttons found under <paramref name="bar"/>'s bridge child; empty when the bar exposes none.</summary>
    IReadOnlyList<TaskbarButton> Buttons(TaskbarBar bar);

    /// <summary>
    /// Monitor bounds of every screen. The bar's own monitor is resolved from these
    /// (<see cref="Dockaroo.Core.Popup.PopupGeometry.ScreenForBar"/>), so tests can cover the
    /// resolution with fake screens.
    /// </summary>
    IReadOnlyList<Rectangle> Screens();
}

/// <summary>
/// UI Automation reads on the real taskbar windows plus the one shared Automation client (also
/// used by the click hit test in <see cref="TaskbarLocator"/>). Win11's taskbar buttons are NOT
/// in the bar window's own UIA tree - Shell_TrayWnd.FindAll returns three panes and no buttons -
/// they are reachable only through the bar's child hwnd of class
/// <see cref="BridgeClassName"/>, which hosts the XAML task list (probe 2026-10-17,
/// FINDINGS.md F-023). Read-only: nothing here clicks, focuses or moves a window.
/// </summary>
internal static class TaskbarUia
{
    /// <summary>Child window class of a taskbar that hosts the buttons' XAML content.</summary>
    public const string BridgeClassName = "Windows.UI.Composition.DesktopWindowContentBridge";

    /// <summary>UIA_BoundingRectanglePropertyId; the value is a double[4]: x, y, width, height.</summary>
    public const int BoundingRectanglePropertyId = 30001;

    private const int AutomationIdPropertyId = 30011;
    private const int TreeScopeSubtree = 7; // element | children | descendants

    private static readonly Lazy<IUIAutomation?> Automation = new(CreateAutomation, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The production implementation over the real taskbar windows.</summary>
    public static ITaskbarUia Windows { get; } = new WindowsTaskbarUia();

    /// <summary>
    /// The element at a screen point as UI Automation sees it, or null when the OS refuses the
    /// read. Runs on the caller's thread; <see cref="TaskbarLocator"/> wraps it in the timeout.
    /// </summary>
    public static Rectangle? ElementRectAt(Point point)
    {
        if (Automation.Value is not { } automation)
        {
            return null;
        }

        try
        {
            var element = automation.ElementFromPoint(new NativePoint { X = point.X, Y = point.Y });
            return element is null ? null : ReadRect(element);
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return null;
        }
    }

    private static IUIAutomation? CreateAutomation()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"));
            return type is null ? null : Activator.CreateInstance(type) as IUIAutomation;
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return null;
        }
    }

    private static string? ReadAutomationId(IUIAutomationElement element)
    {
        return element.GetCurrentPropertyValue(AutomationIdPropertyId) as string;
    }

    private static Rectangle? ReadRect(IUIAutomationElement element)
    {
        return element.GetCurrentPropertyValue(BoundingRectanglePropertyId) is double[] { Length: 4 } r
            ? new Rectangle((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]))
            : null;
    }

    private static bool IsUiaFailure(Exception ex)
    {
        return ex is COMException or InvalidCastException or UnauthorizedAccessException;
    }

    private sealed class WindowsTaskbarUia : ITaskbarUia
    {
        private static readonly (string Class, bool Primary)[] BarClasses =
        {
            ("Shell_TrayWnd", true),
            ("Shell_SecondaryTrayWnd", false),
        };

        public IReadOnlyList<TaskbarBar> Bars()
        {
            var bars = new List<TaskbarBar>();
            foreach (var (className, primary) in BarClasses)
            {
                var window = IntPtr.Zero;
                while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
                {
                    if (GetWindowRect(window, out var rect))
                    {
                        bars.Add(new TaskbarBar(
                            window,
                            Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom),
                            primary));
                    }
                }
            }

            return bars;
        }

        public IReadOnlyList<TaskbarButton> Buttons(TaskbarBar bar)
        {
            if (Automation.Value is not { } automation)
            {
                return Array.Empty<TaskbarButton>();
            }

            // The bar root exposes no buttons; the XAML task list is a hidden child window.
            var bridge = FindWindowEx(bar.Window, IntPtr.Zero, BridgeClassName, null);
            if (bridge == IntPtr.Zero)
            {
                return Array.Empty<TaskbarButton>();
            }

            try
            {
                var root = automation.ElementFromHandle(bridge);
                if (root is null)
                {
                    return Array.Empty<TaskbarButton>();
                }

                var found = root.FindAll(TreeScopeSubtree, automation.CreateTrueCondition());
                var buttons = new List<TaskbarButton>();
                for (var i = 0; i < found.Length; i++)
                {
                    if (found.GetElement(i) is { } element && ReadButton(element) is { } button)
                    {
                        buttons.Add(button);
                    }
                }

                return buttons;
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                return Array.Empty<TaskbarButton>();
            }
        }

        public IReadOnlyList<Rectangle> Screens()
        {
            return System.Windows.Forms.Screen.AllScreens.Select(screen => screen.Bounds).ToList();
        }

        private static TaskbarButton? ReadButton(IUIAutomationElement element)
        {
            try
            {
                var id = ReadAutomationId(element);
                if (string.IsNullOrEmpty(id))
                {
                    return null;
                }

                return ReadRect(element) is { Width: > 0, Height: > 0 } rect
                    ? new TaskbarButton(id, rect)
                    : null;
            }
            catch (Exception ex) when (IsUiaFailure(ex))
            {
                // One unreadable element skips; the rest of the bar still gets walked.
                return null;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    // Minimal UI Automation COM surface: only the slots up to the methods this file calls are
    // declared, in vtable order (UIAutomationClient.h); the placeholders are never called.
    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();

        void CompareRuntimeIds();

        void GetRootElement();

        IUIAutomationElement ElementFromHandle(IntPtr hwnd);

        IUIAutomationElement ElementFromPoint(NativePoint point);

        void GetFocusedElement();

        void GetRootElementBuildCache();

        void ElementFromHandleBuildCache();

        void ElementFromPointBuildCache();

        void GetFocusedElementBuildCache();

        void CreateTreeWalker();

        void GetControlViewWalker();

        void GetContentViewWalker();

        void GetRawViewWalker();

        void GetRawViewCondition();

        void GetControlViewCondition();

        void GetContentViewCondition();

        void CreateCacheRequest();

        IUIAutomationCondition CreateTrueCondition();
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        void FindFirst();

        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }

    [ComImport]
    [Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition
    {
    }

    [ComImport]
    [Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }

        IUIAutomationElement GetElement(int index);
    }
}
