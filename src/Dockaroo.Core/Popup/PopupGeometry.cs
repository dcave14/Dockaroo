using System.Drawing;

namespace Dockaroo.Core.Popup;

/// <summary>Edge of a screen that carries the taskbar.</summary>
public enum TaskbarEdge
{
    /// <summary>No taskbar on this screen (hidden/auto-hide or none).</summary>
    None,

    /// <summary>Taskbar docked on the top edge.</summary>
    Top,

    /// <summary>Taskbar docked on the bottom edge.</summary>
    Bottom,

    /// <summary>Taskbar docked on the left edge.</summary>
    Left,

    /// <summary>Taskbar docked on the right edge.</summary>
    Right,
}

/// <summary>
/// Pure popup math: grid size from entry count and per-row width, and placement near a click
/// point with taskbar-edge awareness. The rules are ported from the legacy popup window
/// (legacy-app Forms/frmMain.cs: <c>LoadCategory</c>, <c>SetLocation</c>, <c>FindDockedTaskBars</c>)
/// and generalized to the screen that contains the cursor instead of guessing by screen index.
/// No IO and no WinForms: callers pass the screen's bounds and working area.
/// </summary>
public static class PopupGeometry
{
    /// <summary>Size of one rounded icon plate in pixels (measured taskbar button size).</summary>
    public const int PlateSize = 40;

    /// <summary>Gap between plates and between grid rows in pixels.</summary>
    public const int PlateGap = 4;

    /// <summary>Horizontal distance from one plate to the next (taskbar buttons sit at 44px pitch).</summary>
    public const int CellPitch = PlateSize + PlateGap;

    /// <summary>Left and right padding inside the popup window.</summary>
    public const int SidePad = 6;

    /// <summary>Top padding inside the popup window.</summary>
    public const int TopPad = 5;

    /// <summary>Bottom padding inside the popup window.</summary>
    public const int BottomPad = 6;

    /// <summary>Vertical distance from one row of plates to the next (the same 44px pitch as the columns).</summary>
    public const int RowPitch = PlateSize + PlateGap;

    /// <summary>Icon size the popup looks up and the resident preloads (8px margin inside the plate).</summary>
    public const int IconSize = 24;

    /// <summary>Gap between the popup and a taskbar or screen edge (legacy 10px).</summary>
    public const int EdgeOffset = 10;

    /// <summary>
    /// How far a taskbar popup tucks under the bar (user ruling 2026-10-15): the popup's 1px trim
    /// and 1px of its bottom padding hide behind the bar, so the trims do not double up and the
    /// visible margin below the plates is 4px - the same inset Windows gives its own buttons.
    /// </summary>
    public const int TaskbarTuck = 2;

    /// <summary>Gap above the click point when the click was not on the taskbar (legacy 20px).</summary>
    public const int CursorOffset = 20;

    /// <summary>Bottom band that counts as a click on a hidden taskbar (legacy 35px).</summary>
    public const int HiddenTaskbarBand = 35;

    /// <summary>Gap above the bottom edge for clicks in the hidden-taskbar band (legacy 45px).</summary>
    public const int HiddenTaskbarOffset = 45;

    /// <summary>
    /// Popup size for <paramref name="entryCount"/> entries shown at most
    /// <paramref name="width"/> per row (the group's Width). The window always fits the real
    /// cells: a single row is sized to the entry count and multi-row grids keep a standard
    /// grid with a possibly partial last row. Width is clamped to the model bounds. Empty for
    /// no entries.
    /// </summary>
    public static Size SizeFor(int entryCount, int width) => SizeFor(entryCount, width, PopupMetrics.Default);

    /// <summary><see cref="SizeFor(int, int)"/> at the scale of <paramref name="metrics"/>.</summary>
    public static Size SizeFor(int entryCount, int width, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (entryCount <= 0)
        {
            return Size.Empty;
        }

        var perRow = PerRow(entryCount, width);
        var rows = (entryCount + perRow - 1) / perRow;
        var popupWidth = (2 * metrics.SidePad) + (perRow * metrics.PlateSize) + ((perRow - 1) * metrics.PlateGap);
        var popupHeight = metrics.TopPad + (rows * metrics.PlateSize) + ((rows - 1) * metrics.PlateGap) + metrics.BottomPad;
        return new Size(popupWidth, popupHeight);
    }

    /// <summary>
    /// Bounds of the plate at <paramref name="index"/>, on the clamped-Width column grid. For
    /// every index below the entry count this matches the window <see cref="SizeFor"/> produces.
    /// </summary>
    public static Rectangle TileBounds(int index, int width) => TileBounds(index, width, PopupMetrics.Default);

    /// <summary><see cref="TileBounds(int, int)"/> at the scale of <paramref name="metrics"/>.</summary>
    public static Rectangle TileBounds(int index, int width, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var perRow = ClampWidth(width);
        var column = index % perRow;
        var row = index / perRow;
        return new Rectangle(
            metrics.SidePad + (column * metrics.CellPitch),
            metrics.TopPad + (row * metrics.CellPitch),
            metrics.PlateSize,
            metrics.PlateSize);
    }

    /// <summary>Entry index under a popup-relative point, or -1 when the point is not on a plate.</summary>
    public static int HitTest(Point point, int entryCount, int width) => HitTest(point, entryCount, width, PopupMetrics.Default);

    /// <summary><see cref="HitTest(Point, int, int)"/> at the scale of <paramref name="metrics"/>.</summary>
    public static int HitTest(Point point, int entryCount, int width, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (entryCount <= 0 || point.X < 0 || point.Y < 0)
        {
            return -1;
        }

        var perRow = PerRow(entryCount, width);
        var x = point.X - metrics.SidePad;
        var y = point.Y - metrics.TopPad;
        if (x < 0 || y < 0)
        {
            return -1;
        }

        var column = x / metrics.CellPitch;
        var row = y / metrics.CellPitch;
        if (column >= perRow
            || x - (column * metrics.CellPitch) >= metrics.PlateSize
            || y - (row * metrics.CellPitch) >= metrics.PlateSize)
        {
            return -1;
        }

        var index = (row * perRow) + column;
        return index < entryCount ? index : -1;
    }

    /// <summary>
    /// Which edge carries the taskbar. Derived from bounds versus working area, in the legacy
    /// <c>FindDockedTaskBars</c> priority: left, right, top, bottom.
    /// </summary>
    public static TaskbarEdge EdgeFor(Rectangle screenBounds, Rectangle workingArea)
    {
        if (workingArea.Left > screenBounds.Left)
        {
            return TaskbarEdge.Left;
        }

        if (workingArea.Right < screenBounds.Right)
        {
            return TaskbarEdge.Right;
        }

        if (workingArea.Top > screenBounds.Top)
        {
            return TaskbarEdge.Top;
        }

        if (workingArea.Bottom < screenBounds.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        return TaskbarEdge.None;
    }

    /// <summary>
    /// The taskbar rectangle this screen's bounds and working area describe, or
    /// <see cref="Rectangle.Empty"/> when the screen has no taskbar (hidden/auto-hide).
    /// </summary>
    public static Rectangle TaskbarRectFor(Rectangle screenBounds, Rectangle workingArea)
    {
        var left = workingArea.Left - screenBounds.Left;
        if (left > 0)
        {
            return new Rectangle(screenBounds.Left, screenBounds.Top, left, screenBounds.Height);
        }

        var right = screenBounds.Right - workingArea.Right;
        if (right > 0)
        {
            return new Rectangle(workingArea.Right, screenBounds.Top, right, screenBounds.Height);
        }

        var top = workingArea.Top - screenBounds.Top;
        if (top > 0)
        {
            return new Rectangle(workingArea.Left, screenBounds.Top, workingArea.Width, top);
        }

        var bottom = screenBounds.Bottom - workingArea.Bottom;
        if (bottom > 0)
        {
            return new Rectangle(workingArea.Left, workingArea.Bottom, workingArea.Width, bottom);
        }

        return Rectangle.Empty;
    }

    /// <summary>
    /// Popup location near <paramref name="cursor"/> on the screen described by
    /// <paramref name="screenBounds"/>/<paramref name="workingArea"/>. Clicks on the taskbar
    /// open next to the bar (centered on the cursor along it); other clicks open above the
    /// cursor (legacy <c>SetLocation</c>). The result is clamped inside the working area on all
    /// four edges; an oversize popup is pinned to the working area's top-left.
    /// </summary>
    public static Point Place(Rectangle screenBounds, Rectangle workingArea, Point cursor, Size popupSize)
        => Place(screenBounds, workingArea, cursor, popupSize, PopupMetrics.Default);

    /// <summary><see cref="Place(Rectangle, Rectangle, Point, Size)"/> with offsets at the scale of <paramref name="metrics"/>.</summary>
    public static Point Place(Rectangle screenBounds, Rectangle workingArea, Point cursor, Size popupSize, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var edgeOffset = metrics.EdgeOffset;
        var bar = TaskbarRectFor(screenBounds, workingArea);
        var edge = EdgeFor(screenBounds, workingArea);

        Point location;
        if (bar.Width > 0 && bar.Contains(cursor))
        {
            location = edge switch
            {
                TaskbarEdge.Top => new Point(cursor.X - (popupSize.Width / 2), bar.Bottom + edgeOffset),
                TaskbarEdge.Bottom => new Point(cursor.X - (popupSize.Width / 2), bar.Top - popupSize.Height - edgeOffset),
                TaskbarEdge.Left => new Point(bar.Right + edgeOffset, cursor.Y - (popupSize.Height / 2)),
                _ => new Point(bar.Left - popupSize.Width - edgeOffset, cursor.Y - (popupSize.Height / 2)),
            };
        }
        else if (edge == TaskbarEdge.None)
        {
            // Hidden taskbar (legacy auto-hide branch): clicks in the bottom band open above
            // the band, everything else above the cursor. Unlike the legacy code this uses
            // the screen under the cursor, so it is correct on secondary monitors too.
            var y = cursor.Y > screenBounds.Bottom - metrics.HiddenTaskbarBand
                ? screenBounds.Bottom - popupSize.Height - metrics.HiddenTaskbarOffset
                : cursor.Y - popupSize.Height - metrics.CursorOffset;
            location = new Point(cursor.X - (popupSize.Width / 2), y);
        }
        else
        {
            // Not clicked on the taskbar: open above the click point (legacy SetLocation).
            location = new Point(cursor.X - (popupSize.Width / 2), cursor.Y - popupSize.Height - metrics.CursorOffset);
        }

        // Keep the popup inside the working area on all four edges (the legacy code clamped
        // left, top and right against the screen, so side-taskbar popups could open off the
        // bottom). The working area excludes the taskbar, so no separate push-off-the-bar step.
        location.X = ClampAxis(location.X, popupSize.Width, workingArea.Left, workingArea.Right, edgeOffset);
        location.Y = ClampAxis(location.Y, popupSize.Height, workingArea.Top, workingArea.Bottom, edgeOffset);

        return location;
    }

    /// <summary>
    /// Per-row count to lay out <paramref name="entryCount"/> entries so the popup fits inside
    /// <paramref name="workingArea"/> (with the <see cref="EdgeOffset"/> margin): starts from the
    /// group's Width, drops columns while the popup is too wide, then adds columns while it is
    /// too tall and a wider grid still fits. Returns the clamped Width when nothing needs fixing.
    /// </summary>
    public static int FitPerRow(int entryCount, int width, Rectangle workingArea)
        => FitPerRow(entryCount, width, workingArea, PopupMetrics.Default);

    /// <summary><see cref="FitPerRow(int, int, Rectangle)"/> at the scale of <paramref name="metrics"/>.</summary>
    public static int FitPerRow(int entryCount, int width, Rectangle workingArea, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var perRow = ClampWidth(width);
        if (entryCount <= 0)
        {
            return perRow;
        }

        var maxWidth = workingArea.Width - (2 * metrics.EdgeOffset);
        var maxHeight = workingArea.Height - (2 * metrics.EdgeOffset);
        while (perRow > Group.MinWidth && SizeFor(entryCount, perRow, metrics).Width > maxWidth)
        {
            perRow--;
        }

        while (perRow < Math.Min(entryCount, Group.MaxWidth)
            && SizeFor(entryCount, perRow, metrics).Height > maxHeight
            && SizeFor(entryCount, perRow + 1, metrics).Width <= maxWidth)
        {
            perRow++;
        }

        return perRow;
    }

    // Prefers an EdgeOffset gap from the near edge (legacy clamp), never leaves [min, max]
    // when the popup fits, and pins an oversize popup to the start edge.
    /// <summary>
    /// Placement for a click on a taskbar button (user ruling 2026-10-15: "always centered and
    /// flush"). The popup sits flush against the bar's inner edge - touching it, never overlapping
    /// it - and is centred on <paramref name="buttonCenter"/> along the bar. It only moves off
    /// centre when it would leave <paramref name="screenBounds"/>. <paramref name="bar"/> is the
    /// taskbar's real rectangle at click time, which also covers an auto-hide bar while it is
    /// revealed (the working area cannot describe that one).
    /// </summary>
    public static Point PlaceOnTaskbar(Rectangle screenBounds, Rectangle bar, int buttonCenter, Size popupSize)
        => PlaceOnTaskbar(screenBounds, bar, buttonCenter, popupSize, PopupMetrics.Default);

    /// <summary>
    /// <see cref="PlaceOnTaskbar(Rectangle, Rectangle, int, Size)"/> at the scale of
    /// <paramref name="metrics"/>: the popup's bar-side edge tucks <see cref="PopupMetrics.TaskbarTuck"/>
    /// pixels under the bar (the popup is placed below the bar in z-order, so the bar covers it).
    /// </summary>
    public static Point PlaceOnTaskbar(Rectangle screenBounds, Rectangle bar, int buttonCenter, Size popupSize, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var tuck = metrics.TaskbarTuck;
        var edge = EdgeOfBar(screenBounds, bar);
        Point location = edge switch
        {
            TaskbarEdge.Top => new Point(buttonCenter - (popupSize.Width / 2), bar.Bottom - tuck),
            TaskbarEdge.Left => new Point(bar.Right - tuck, buttonCenter - (popupSize.Height / 2)),
            TaskbarEdge.Right => new Point(bar.Left - popupSize.Width + tuck, buttonCenter - (popupSize.Height / 2)),
            _ => new Point(buttonCenter - (popupSize.Width / 2), bar.Top - popupSize.Height + tuck),
        };

        // Centred unless that would leave the screen; flush to the bar either way.
        if (edge is TaskbarEdge.Left or TaskbarEdge.Right)
        {
            location.Y = Math.Max(screenBounds.Top, Math.Min(location.Y, screenBounds.Bottom - popupSize.Height));
        }
        else
        {
            location.X = Math.Max(screenBounds.Left, Math.Min(location.X, screenBounds.Right - popupSize.Width));
        }

        return location;
    }

    /// <summary>
    /// The part of a tucked popup that stays visible: everything except the
    /// <see cref="PopupMetrics.TaskbarTuck"/> strip on the bar side. The window is clipped to it,
    /// so the bar shows through that strip whatever the z-order is (popup-relative coordinates).
    /// </summary>
    public static Rectangle VisibleWhenTucked(Size popupSize, TaskbarEdge edge, PopupMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var tuck = Math.Min(metrics.TaskbarTuck, Math.Max(0, Math.Min(popupSize.Width, popupSize.Height) - 1));
        return edge switch
        {
            TaskbarEdge.Top => new Rectangle(0, tuck, popupSize.Width, popupSize.Height - tuck),
            TaskbarEdge.Left => new Rectangle(tuck, 0, popupSize.Width - tuck, popupSize.Height),
            TaskbarEdge.Right => new Rectangle(0, 0, popupSize.Width - tuck, popupSize.Height),
            _ => new Rectangle(0, 0, popupSize.Width, popupSize.Height - tuck),
        };
    }

    /// <summary>
    /// True while the taskbar still covers the popup's tucked strip (screen coordinates): the
    /// strip stays clipped so the bar's trim is the only line. False once an auto-hide bar has
    /// slid away - the popup then shows its own bar-side trim instead of a cut-off edge.
    /// </summary>
    public static bool BarCoversTuck(Rectangle popupBounds, Rectangle bar)
    {
        return !bar.IsEmpty && popupBounds.IntersectsWith(bar);
    }

    /// <summary>Which screen edge a taskbar rectangle is docked to (by shape, then by position).</summary>
    public static TaskbarEdge EdgeOfBar(Rectangle screenBounds, Rectangle bar)
    {
        var screenCenter = new Point(screenBounds.Left + (screenBounds.Width / 2), screenBounds.Top + (screenBounds.Height / 2));
        if (bar.Width >= bar.Height)
        {
            return bar.Top + (bar.Height / 2) < screenCenter.Y ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        return bar.Left + (bar.Width / 2) < screenCenter.X ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    /// <summary>
    /// The revealed form of a taskbar rectangle: a parked auto-hide bar reports its window rect
    /// as a sliver off the monitor edge (measured 46px below the revealed bar on a 1080p bottom
    /// bar while parked), because Windows slides the whole window away instead of hiding it.
    /// The side the rect overhangs is the edge the bar is docked to (pick the right monitor
    /// with <see cref="ScreenForBar"/> first); the rectangle is snapped flush to that edge,
    /// same size. A fully contained (revealed) bar is already flush and comes back unchanged.
    /// </summary>
    public static Rectangle RevealedBar(Rectangle bar, Rectangle screenBounds)
    {
        if (bar.IsEmpty || screenBounds.IsEmpty)
        {
            return bar;
        }

        if (bar.Bottom > screenBounds.Bottom)
        {
            return new Rectangle(bar.X, screenBounds.Bottom - bar.Height, bar.Width, bar.Height);
        }

        if (bar.Top < screenBounds.Top)
        {
            return new Rectangle(bar.X, screenBounds.Top, bar.Width, bar.Height);
        }

        if (bar.Left < screenBounds.Left)
        {
            return new Rectangle(screenBounds.Left, bar.Y, bar.Width, bar.Height);
        }

        if (bar.Right > screenBounds.Right)
        {
            return new Rectangle(screenBounds.Right - bar.Width, bar.Y, bar.Width, bar.Height);
        }

        return bar;
    }

    /// <summary>
    /// The monitor a taskbar rectangle belongs to: the screen that contains the rect when one
    /// does (a revealed bar), else the screen with the smallest non-zero intersection. A parked
    /// auto-hide bar shows a 2px sliver on its own monitor while its 46px overhang reaches into
    /// the neighbour, so the usual largest-intersection pick resolves it to the NEIGHBOUR on a
    /// stacked-monitor layout - the snap then goes to the wrong edge and the popup falls back to
    /// the cursor. Empty when no screen intersects the rect.
    /// </summary>
    public static Rectangle ScreenForBar(Rectangle bar, IReadOnlyList<Rectangle> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (bar.IsEmpty)
        {
            return Rectangle.Empty;
        }

        foreach (var screen in screens)
        {
            if (screen.Contains(bar))
            {
                return screen;
            }
        }

        var best = Rectangle.Empty;
        var bestArea = long.MaxValue;
        foreach (var screen in screens)
        {
            var overlap = Rectangle.Intersect(bar, screen);
            var area = (long)overlap.Width * overlap.Height;
            if (area > 0 && area < bestArea)
            {
                best = screen;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// A rectangle carried by a bar's parked -> revealed move (<see cref="RevealedBar"/>): the
    /// same offset inside the bar, reapplied to the revealed bar. Brings a button's rect - and
    /// so its centre along the bar - up with its bar without touching its size.
    /// </summary>
    public static Rectangle MovedWithBar(Rectangle part, Rectangle parkedBar, Rectangle revealedBar)
    {
        return new Rectangle(
            revealedBar.Left + (part.Left - parkedBar.Left),
            revealedBar.Top + (part.Top - parkedBar.Top),
            part.Width,
            part.Height);
    }

    private static int ClampAxis(int position, int length, int min, int max, int edgeOffset)
    {
        if (position < min)
        {
            position = min + edgeOffset;
        }

        if (position + length > max)
        {
            position = max - length - edgeOffset;
        }

        return Math.Max(min, Math.Min(position, max - length));
    }

    private static int PerRow(int entryCount, int width)
    {
        return Math.Min(entryCount, ClampWidth(width));
    }

    private static int ClampWidth(int width)
    {
        return Math.Clamp(width, Group.MinWidth, Group.MaxWidth);
    }
}
