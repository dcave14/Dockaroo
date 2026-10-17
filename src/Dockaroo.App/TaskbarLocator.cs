using System.Drawing;
using System.Runtime.InteropServices;
using Dockaroo.Core;
using Dockaroo.Core.Linking;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>Where a taskbar click happened: the bar's real rectangle and the clicked button's centre.</summary>
/// <param name="Bar">Taskbar window rectangle at click time (revealed, even when auto-hide is on).</param>
/// <param name="ButtonCenter">Centre of the clicked button along the bar (the cursor when not found).</param>
/// <param name="ButtonFound">True when the click's or the group's own button was found by UI Automation.</param>
internal sealed record TaskbarAnchor(Rectangle Bar, int ButtonCenter, bool ButtonFound)
{
    /// <summary>The taskbar's window, so the popup can sit just below it in z-order.</summary>
    public IntPtr BarWindow { get; init; }
}

/// <summary>
/// Finds the taskbar a click landed on and the button a popup should anchor to, so the popup
/// can sit flush on the bar and centred on its own button. Window queries plus UI Automation
/// reads - no file IO. <see cref="Locate"/> is the click path (a hit test under the captured
/// cursor, capped at <see cref="ButtonLookupTimeoutMs"/> on the pipe thread);
/// <see cref="LocateForGroup"/> is the cursor-independent path for a popup whose group has a
/// pinned taskbar button (the mouse may have swiped away mid-handoff, or Win+N can launch from
/// anywhere).
/// </summary>
internal static class TaskbarLocator
{
    /// <summary>Longest the popup waits for the button lookup.</summary>
    public const int ButtonLookupTimeoutMs = 150;

    /// <summary>
    /// The taskbar under <paramref name="cursor"/> and its clicked button, or null when the click
    /// was not on a taskbar (keyboard launch, tray menu elsewhere). <paramref name="findButton"/>
    /// false skips the hit test (UI-thread callers).
    /// </summary>
    public static TaskbarAnchor? Locate(Point cursor, bool findButton = true)
    {
        var bars = TaskbarUia.Windows.Bars();
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

    /// <summary>
    /// The group's own taskbar button wherever the captured cursor is - or without one at all:
    /// only the bar tie-break uses the cursor hint, so a cursor-less request (a legacy payload)
    /// still anchors to the button. The AutomationId match walks every taskbar on a worker
    /// thread under the same <see cref="ButtonLookupTimeoutMs"/> cap as the hit test; null when
    /// nothing matches, the group name cannot have a pin, or the cap is hit - the caller then
    /// keeps its existing cursor-based anchor. <paramref name="uia"/> is a test seam (null uses
    /// the real taskbar windows).
    /// </summary>
    public static TaskbarAnchor? LocateForGroup(Point? cursor, string groupName, ITaskbarUia? uia = null)
    {
        if (ExpectedAutomationId(groupName) is null)
        {
            return null;
        }

        var lookup = Task.Run(() => AnchorForGroup(cursor, groupName, uia ?? TaskbarUia.Windows));
        try
        {
            return lookup.Wait(ButtonLookupTimeoutMs) ? lookup.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    /// <summary>
    /// Matches a group's pinned button across every bar by its AutomationId and returns the
    /// anchor for it. Bar pick when several bars carry the same button (the probe saw every
    /// group on both bars): the bar whose horizontal range contains the captured cursor.X (a
    /// vertical swipe keeps X), then the primary bar, then the first found; without a captured
    /// cursor the X hint is skipped. A parked bar is snapped to its revealed rect (and the
    /// button with it) before anchoring, so the popup never anchors to an off-screen sliver.
    /// Never throws; UI Automation failures give null.
    /// </summary>
    internal static TaskbarAnchor? AnchorForGroup(Point? cursor, string groupName, ITaskbarUia uia)
    {
        var expected = ExpectedAutomationId(groupName);
        if (expected is null)
        {
            return null;
        }

        try
        {
            var screens = uia.Screens();
            var matches = new List<(TaskbarAnchor Anchor, bool IsPrimary)>();
            foreach (var bar in uia.Bars())
            {
                // The bar's own monitor: containment when revealed, else the smallest non-zero
                // intersection (its visible sliver). The largest intersection would pick the
                // neighbour a parked bar's overhang reaches into on a stacked layout.
                var screenBounds = PopupGeometry.ScreenForBar(bar.Rect, screens);
                if (screenBounds.IsEmpty)
                {
                    continue;
                }

                var revealedBar = PopupGeometry.RevealedBar(bar.Rect, screenBounds);
                foreach (var button in uia.Buttons(bar))
                {
                    if (!Matches(button.AutomationId, expected) || button.Rect.Width <= 0 || button.Rect.Height <= 0)
                    {
                        continue;
                    }

                    var revealedButton = PopupGeometry.MovedWithBar(button.Rect, bar.Rect, revealedBar);
                    matches.Add((
                        new TaskbarAnchor(revealedBar, AxisCentre(revealedButton, revealedBar), ButtonFound: true) { BarWindow = bar.Window },
                        bar.IsPrimary));
                }
            }

            return ChooseMatch(matches, cursor)?.Anchor;
        }
        catch (Exception ex) when (IsUiaFailure(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The AutomationId a group's taskbar button carries (probe 2026-10-17): "Appid: " plus the
    /// .lnk AppUserModelID - <see cref="GroupLnkWriter.DeriveAppUserModelId"/> does the spaces to
    /// underscores transform, never duplicated here. Null for a name that cannot have a pin.
    /// </summary>
    internal static string? ExpectedAutomationId(string? groupName)
    {
        return Group.IsValidName(groupName)
            ? "Appid: " + GroupLnkWriter.DeriveAppUserModelId(groupName!)
            : null;
    }

    /// <summary>True when a taskbar button's AutomationId is this group's: trimmed, ordinal-ignore-case.</summary>
    internal static bool MatchesGroup(string? automationId, string? groupName)
    {
        return automationId is not null
            && ExpectedAutomationId(groupName) is { } expected
            && Matches(automationId, expected);
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

        return AxisCentre(element, bar);
    }

    /// <summary>Test hook: the UI Automation hit test on its own.</summary>
    internal static Rectangle? ElementRectAt(Point point) => TryElementRectAt(point);

    private static bool Matches(string automationId, string expected)
    {
        return string.Equals(automationId.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }

    private static (TaskbarAnchor Anchor, bool IsPrimary)? ChooseMatch(
        IReadOnlyList<(TaskbarAnchor Anchor, bool IsPrimary)> matches,
        Point? cursor)
    {
        if (cursor is { } point)
        {
            foreach (var match in matches)
            {
                if (ContainsX(match.Anchor.Bar, point.X))
                {
                    return match;
                }
            }
        }

        foreach (var match in matches)
        {
            if (match.IsPrimary)
            {
                return match;
            }
        }

        return matches.Count > 0 ? matches[0] : null;
    }

    private static bool ContainsX(Rectangle bar, int x)
    {
        return x >= bar.Left && x < bar.Right;
    }

    /// <summary>Centre of a matched button along the bar; unlike <see cref="ButtonCenter"/> no cursor validation - the match is the proof.</summary>
    private static int AxisCentre(Rectangle button, Rectangle bar)
    {
        return bar.Width >= bar.Height
            ? button.Left + (button.Width / 2)
            : button.Top + (button.Height / 2);
    }

    private static Rectangle? TryElementRectAt(Point point)
    {
        var lookup = Task.Run(() => TaskbarUia.ElementRectAt(point));
        return lookup.Wait(ButtonLookupTimeoutMs) ? lookup.Result : null;
    }

    private static bool IsUiaFailure(Exception ex)
    {
        return ex is COMException or InvalidCastException or UnauthorizedAccessException;
    }
}
