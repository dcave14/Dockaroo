using System.Drawing;
using System.Runtime.InteropServices;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>Where a taskbar click happened: the bar's real rectangle and the clicked button's centre.</summary>
/// <param name="Bar">Taskbar window rectangle at click time (revealed, even when auto-hide is on).</param>
/// <param name="ButtonCenter">Centre of the clicked button along the bar (the cursor when not found).</param>
/// <param name="ButtonFound">True when UI Automation reported the button under the cursor.</param>
internal sealed record TaskbarAnchor(Rectangle Bar, int ButtonCenter, bool ButtonFound)
{
    /// <summary>The taskbar's window, so the popup can sit just below it in z-order.</summary>
    public IntPtr BarWindow { get; init; }
}

/// <summary>
/// Finds the taskbar a click landed on and the button under the cursor, so the popup can sit
/// flush on the bar and centred on its own button. Window queries plus one UI Automation hit
/// test - no file IO. Called off the UI thread (the pipe listener) before the request is posted;
/// the hit test is capped at <see cref="ButtonLookupTimeoutMs"/> and falls back to the cursor.
/// </summary>
internal static class TaskbarLocator
{
    /// <summary>Longest the popup waits for the button hit test.</summary>
    public const int ButtonLookupTimeoutMs = 150;

    private const int UiaBoundingRectanglePropertyId = 30001;
    private static readonly Lazy<IUIAutomation?> Automation = new(CreateAutomation, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The taskbar under <paramref name="cursor"/> and its clicked button, or null when the click
    /// was not on a taskbar (keyboard launch, tray menu elsewhere). <paramref name="findButton"/>
    /// false skips the hit test (UI-thread callers).
    /// </summary>
    public static TaskbarAnchor? Locate(Point cursor, bool findButton = true)
    {
        var bars = TaskbarWindows();
        var bar = ChooseBar(bars.Select(b => b.Rect), cursor);
        if (bar is not { } barRect)
        {
            return null;
        }

        var window = bars.First(b => b.Rect == barRect).Window;

        var horizontal = barRect.Width >= barRect.Height;
        var fallback = horizontal ? cursor.X : cursor.Y;
        if (findButton && TryElementRectAt(cursor) is { } element && ButtonCenter(element, barRect, cursor) is { } center)
        {
            return new TaskbarAnchor(barRect, center, ButtonFound: true) { BarWindow = window };
        }

        return new TaskbarAnchor(barRect, fallback, ButtonFound: false) { BarWindow = window };
    }

    /// <summary>Loads UI Automation in the background so the first click does not pay for it.</summary>
    public static void WarmUp()
    {
        _ = Task.Run(() => TryElementRectAt(new Point(0, 0)));
    }

    /// <summary>The taskbar rectangle that contains the cursor (the clicked, revealed bar).</summary>
    internal static Rectangle? ChooseBar(IEnumerable<Rectangle> bars, Point cursor)
    {
        foreach (var bar in bars)
        {
            if (bar.Width > 0 && bar.Height > 0 && bar.Contains(cursor))
            {
                return bar;
            }
        }

        return null;
    }

    /// <summary>
    /// Centre of a hit-tested element along the bar when it is plausibly a taskbar button: it
    /// contains the cursor, lies inside the bar, and is no wider (along the bar) than three bar
    /// thicknesses - so a hit on the whole task list or the bar itself is rejected.
    /// </summary>
    internal static int? ButtonCenter(Rectangle element, Rectangle bar, Point cursor)
    {
        if (element.Width <= 0 || element.Height <= 0 || !element.Contains(cursor))
        {
            return null;
        }

        var inside = Rectangle.Inflate(bar, 2, 2);
        if (!inside.Contains(element))
        {
            return null;
        }

        var horizontal = bar.Width >= bar.Height;
        var thickness = horizontal ? bar.Height : bar.Width;
        var length = horizontal ? element.Width : element.Height;
        if (length > thickness * 3 || length < thickness / 3)
        {
            return null;
        }

        return horizontal ? element.Left + (element.Width / 2) : element.Top + (element.Height / 2);
    }

    /// <summary>Test hook: the UI Automation hit test on its own.</summary>
    internal static Rectangle? ElementRectAt(Point point) => TryElementRectAt(point);

    private static List<(Rectangle Rect, IntPtr Window)> TaskbarWindows()
    {
        var bars = new List<(Rectangle Rect, IntPtr Window)>();
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var window = IntPtr.Zero;
            while ((window = FindWindowEx(IntPtr.Zero, window, className, null)) != IntPtr.Zero)
            {
                if (GetWindowRect(window, out var r))
                {
                    bars.Add((Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), window));
                }
            }
        }

        return bars;
    }

    private static Rectangle? TryElementRectAt(Point point)
    {
        if (Automation.Value is not { } automation)
        {
            return null;
        }

        var lookup = Task.Run(() =>
        {
            try
            {
                var element = automation.ElementFromPoint(new NativePoint { X = point.X, Y = point.Y });
                return element?.GetCurrentPropertyValue(UiaBoundingRectanglePropertyId) is double[] { Length: 4 } r
                    ? new Rectangle((int)Math.Round(r[0]), (int)Math.Round(r[1]), (int)Math.Round(r[2]), (int)Math.Round(r[3]))
                    : (Rectangle?)null;
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
            {
                return null;
            }
        });

        return lookup.Wait(ButtonLookupTimeoutMs) ? lookup.Result : null;
    }

    private static IUIAutomation? CreateAutomation()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"));
            return type is null ? null : Activator.CreateInstance(type) as IUIAutomation;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            return null;
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

        void ElementFromHandle();

        IUIAutomationElement ElementFromPoint(NativePoint point);
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();

        void GetRuntimeId();

        void FindFirst();

        void FindAll();

        void FindFirstBuildCache();

        void FindAllBuildCache();

        void BuildUpdatedCache();

        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }
}
