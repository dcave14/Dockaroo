using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Launch;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>
/// The group popup: a borderless grid of icon plates (one per shortcut), with no text. It
/// renders only from data preloaded at resident startup (the group object and
/// <see cref="IconCache.TryGet"/>), so showing and painting it performs no file, registry or
/// shell IO. Layout, hotkeys and close-on-launch semantics follow the legacy popup window
/// (legacy-app Forms/frmMain.cs). When the group has a matching taskbar pin (decided off the show
/// path), the popup registers as a normal app window carrying the group's AppUserModelID, so
/// the taskbar merges it under the pin and shows its persistent selected state while the
/// popup is open.
/// </summary>
internal sealed partial class PopupForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const ushort VtBstr = 8;
    private const ushort VtLpWstr = 31;
    private const int PropVariantUnionOffset = 8;

    private static readonly Guid IidIPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");


    private readonly Group _group;
    private readonly IconCache _icons;
    private readonly IconSource?[] _sources;
    private readonly PopupPainter _painter;
    private readonly string? _pinnedAppUserModelId;

    private readonly PopupKeyState _keys = new();

    // Previous-key-state bit (30) of the WM_KEYDOWN being dispatched; read by OnKeyDown.
    private bool _keyDownWasRepeat;

    // Columns actually laid out: the group's Width, narrowed or widened to fit the screen.
    private readonly int _perRow;

    // Physical-pixel measurements for the anchor monitor's DPI (PerMonitorV2).
    private readonly PopupMetrics _metrics;

    // The clicked taskbar's window: the popup stays just below it in z-order so the bar covers
    // the tucked bottom strip (one trim line, not two). Zero for non-taskbar opens.
    private readonly IntPtr _barWindow;
    private int _hoveredIndex = -1;

    /// <summary>
    /// <paramref name="metrics"/> overrides the scale derived from the anchor monitor's DPI
    /// (harnesses render 150%/200% popups on a 100% desktop with it).
    /// </summary>
    public PopupForm(Group group, IconCache icons, Point anchor, string? pinnedAppUserModelId = null, PopupMetrics? metrics = null, TaskbarAnchor? taskbar = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(icons);

        _group = group;
        _icons = icons;
        _sources = BuildSources(group.Shortcuts);
        _pinnedAppUserModelId = string.IsNullOrEmpty(pinnedAppUserModelId) ? null : pinnedAppUserModelId;

        Text = AppConstants.PopupTitle;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;

        // Sizes are physical pixels computed from PopupMetrics; WinForms must not rescale them.
        AutoScaleMode = AutoScaleMode.None;
        _metrics = metrics ?? PopupMetrics.ForDpi(MonitorDpi.ForPoint(anchor));

        // Legacy look: background color and transparency come from the group (frmMain ctor).
        // The Dark theme (the measured default) also gets the taskbar-matched surface.
        _painter = new PopupPainter(group.ColorHex);
        BackColor = _painter.BackColor;
        Opacity = PopupPainter.OpacityFor(group.Opacity);

        var screen = Screen.FromPoint(anchor);
        _perRow = PopupGeometry.FitPerRow(group.Shortcuts.Count, group.Width, screen.WorkingArea, _metrics);
        var popupSize = PopupGeometry.SizeFor(group.Shortcuts.Count, _perRow, _metrics);
        ClientSize = popupSize;
        _barWindow = taskbar?.BarWindow ?? IntPtr.Zero;

        // A taskbar click: on the bar (tucked under it), centred on the clicked button.
        // Anything else keeps the cursor-based placement.
        if (taskbar is { } bar && screen.Bounds.IntersectsWith(bar.Bar))
        {
            Location = PopupGeometry.PlaceOnTaskbar(screen.Bounds, bar.Bar, bar.ButtonCenter, popupSize, _metrics);

            // The strip that sits under the bar is cut out of the window itself, so the bar's own
            // trim is the only line there - even if Windows raises the popup above the taskbar.
            var edge = PopupGeometry.EdgeOfBar(screen.Bounds, bar.Bar);
            _tuckClip = PopupGeometry.VisibleWhenTucked(popupSize, edge, _metrics);
            ApplyTuckClip(true);
            StartBarWatch();
        }
        else
        {
            Location = PopupGeometry.Place(screen.Bounds, screen.WorkingArea, anchor, popupSize, _metrics);
        }
    }

    /// <summary>
    /// When a matching pin exists, the popup must be a normal app window (WS_EX_APPWINDOW
    /// without WS_EX_TOOLWINDOW) for the taskbar to merge it under the pin; otherwise the
    /// legacy ShowInTaskbar=false tool-window form is kept, because an app window with no
    /// matching pin would flash its own taskbar button.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            if (_pinnedAppUserModelId is not null)
            {
                createParams.ExStyle &= ~WsExToolWindow;
                createParams.ExStyle |= WsExAppWindow;
            }
            else
            {
                // Unpinned popups must never get a taskbar button: keep the tool-window style.
                createParams.ExStyle |= WsExToolWindow;
                createParams.ExStyle &= ~WsExAppWindow;
            }

            return createParams;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_pinnedAppUserModelId is not null)
        {
            // The only setup on the show path: a one-time COM property set, no file IO.
            TrySetWindowAppUserModelId(Handle, _pinnedAppUserModelId);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _painter.Paint(
            e.Graphics,
            ClientSize,
            _metrics,
            _group.Shortcuts.Count,
            _perRow,
            _hoveredIndex,
            IconFor,
            i => _group.Shortcuts[i].Name,
            Font);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHovered(PopupGeometry.HitTest(e.Location, _group.Shortcuts.Count, _perRow, _metrics));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(-1);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var index = PopupGeometry.HitTest(e.Location, _group.Shortcuts.Count, _perRow, _metrics);
        if (index >= 0)
        {
            Launch(index);
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        if (m.Msg is WmKeyDown or WmSysKeyDown)
        {
            _keyDownWasRepeat = ((long)m.LParam & (1L << 30)) != 0;
        }

        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var firstPress = _keys.Press(e.KeyCode, _keyDownWasRepeat);
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.Control)
        {
            // Legacy frmMain_KeyUp: Ctrl+Enter opens every entry, but only when the group allows
            // it. Handled on the first KeyDown so the order the keys are released in does not
            // matter and auto-repeat does not launch everything twice.
            if (firstPress && _group.AllowOpenAll)
            {
                LaunchAll();
            }

            e.Handled = true;
        }
        else
        {
            var index = IndexForDigitKey(e.KeyCode);
            if (index >= 0 && index < _group.Shortcuts.Count)
            {
                // Legacy hotkey feedback: pressing the digit highlighted the tile.
                SetHovered(index);
                e.Handled = true;
            }
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Only keys pressed while this popup had focus act: the KeyUp of the Win+N that opened
        // a pinned group must not launch entry N.
        if (_keys.Release(e.KeyCode))
        {
            var index = IndexForDigitKey(e.KeyCode);
            if (index >= 0 && index < _group.Shortcuts.Count)
            {
                SetHovered(-1);
                Launch(index);
                e.Handled = true;
            }
        }

        base.OnKeyUp(e);
    }

    /// <summary>
    /// The popup is laid out for one monitor's DPI and lives for a click; if the DPI changes
    /// under it (display settings, or a drag to another monitor) it closes instead of being
    /// rescaled by WinForms into the wrong geometry.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // Same DPI as the layout (e.g. raised during the first show): nothing to do.
        if (e.DeviceDpiNew == (int)Math.Round(_metrics.Scale * PopupMetrics.BaseDpi))
        {
            base.OnDpiChanged(e);
            return;
        }

        e.Cancel = true;
        base.OnDpiChanged(e);
        if (Visible && !IsDisposed)
        {
            Close();
        }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // Activation lifts a topmost window above every other topmost one, the taskbar included;
        // drop back to just below the bar (still topmost, still active, nothing else moves).
        TuckBelowTaskbar();
    }

    /// <summary>Places the popup directly below the clicked taskbar window in z-order.</summary>
    internal void TuckBelowTaskbar()
    {
        if (_barWindow == IntPtr.Zero || !IsHandleCreated)
        {
            return;
        }

        const uint SwpNoSize = 0x0001;
        const uint SwpNoMove = 0x0002;
        const uint SwpNoActivate = 0x0010;
        const uint SwpNoOwnerZOrder = 0x0200;
        _ = SetWindowPos(Handle, _barWindow, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !IsDisposed)
        {
            // Legacy frmMain_Deactivate: clicking outside (or the launched app taking focus)
            // closes the popup.
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopBarWatch();
            _painter.Dispose();
        }

        // The clip region is ours: WinForms disposes a Region only when another replaces it.
        var clip = disposing ? Region : null;
        base.Dispose(disposing);
        clip?.Dispose();
    }

    private static IconSource?[] BuildSources(IReadOnlyList<ShortcutEntry> shortcuts)
    {
        var sources = new IconSource?[shortcuts.Count];
        for (var i = 0; i < shortcuts.Count; i++)
        {
            sources[i] = TryCreateIconSource(shortcuts[i].TargetPath);
        }

        return sources;
    }

    /// <summary>Pure string math; a bad path never crashes the popup, it just shows a placeholder.</summary>
    private static IconSource? TryCreateIconSource(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        try
        {
            return IconSource.FromPath(targetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Memory-only: the icon at this monitor's size, else the always-preloaded 100% icon drawn
    // scaled (a display change can add a scale before its background preload ends).
    private Bitmap? IconFor(int index)
    {
        return _sources[index] is { } source
            ? TryGetBitmap(source, _metrics.IconSize) ?? TryGetBitmap(source, PopupMetrics.Default.IconSize)
            : null;
    }

    private Bitmap? TryGetBitmap(IconSource source, int size)
    {
        return _icons.TryGet(source, size, out var result) ? result?.Bitmap : null;
    }

    /// <summary>Kept for tests that render the taskbar surface; see <see cref="PopupPainter"/>.</summary>
    internal static void PaintTaskbarBackground(Graphics graphics, Size size) => PopupPainter.PaintTaskbarBackground(graphics, size);

    /// <summary>
    /// Sets PKEY_AppUserModel_ID on a window through SHGetPropertyStoreForWindow (the one-time
    /// setup at popup handle creation). Returns false when the shell rejects the window or the
    /// property set.
    /// </summary>
    internal static bool TrySetWindowAppUserModelId(IntPtr hwnd, string appUserModelId)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        var iid = IidIPropertyStore;
        if (NativeMethods.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) < 0 || store is null)
        {
            return false;
        }

        try
        {
            var key = PropertyKeys.AppUserModelId;
            var buffer = Marshal.AllocHGlobal(PropVariantSize);
            try
            {
                // Zero the PROPVARIANT (VT_EMPTY) first so the clear in the finally block stays
                // safe even when the string allocation or SetValue fails before writing.
                for (var offset = 0; offset < PropVariantSize; offset += IntPtr.Size)
                {
                    Marshal.WriteIntPtr(buffer, offset, IntPtr.Zero);
                }

                Marshal.WriteInt16(buffer, 0, (short)VtLpWstr);
                Marshal.WriteIntPtr(buffer, PropVariantUnionOffset, Marshal.StringToCoTaskMemUni(appUserModelId));
                if (store.SetValue(ref key, buffer) < 0)
                {
                    return false;
                }

                return store.Commit() >= 0;
            }
            finally
            {
                NativeMethods.PropVariantClear(buffer);
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return false;
        }
        finally
        {
            ReleaseComObject(store);
        }
    }

    /// <summary>
    /// Reads PKEY_AppUserModel_ID back from a window's property store (verification helper for
    /// the tests; false when the window carries none or the shell refuses the read).
    /// </summary>
    internal static bool TryGetWindowAppUserModelId(IntPtr hwnd, out string? appUserModelId)
    {
        appUserModelId = null;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var iid = IidIPropertyStore;
        if (NativeMethods.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) < 0 || store is null)
        {
            return false;
        }

        try
        {
            var key = PropertyKeys.AppUserModelId;
            var buffer = Marshal.AllocHGlobal(PropVariantSize);
            try
            {
                for (var offset = 0; offset < PropVariantSize; offset += IntPtr.Size)
                {
                    Marshal.WriteIntPtr(buffer, offset, IntPtr.Zero);
                }

                if (store.GetValue(ref key, buffer) < 0)
                {
                    return false;
                }

                var valueType = (ushort)Marshal.ReadInt16(buffer);
                if (valueType != VtLpWstr && valueType != VtBstr)
                {
                    return false;
                }

                var pointer = Marshal.ReadIntPtr(buffer, PropVariantUnionOffset);
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                appUserModelId = valueType == VtLpWstr
                    ? Marshal.PtrToStringUni(pointer)
                    : Marshal.PtrToStringBSTR(pointer);
                return !string.IsNullOrEmpty(appUserModelId);
            }
            finally
            {
                NativeMethods.PropVariantClear(buffer);
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (IsInteropFailure(ex))
        {
            return false;
        }
        finally
        {
            ReleaseComObject(store);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is null)
        {
            return;
        }

        try
        {
            Marshal.FinalReleaseComObject(instance);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            // Best effort; the result is already decided.
        }
    }

    private static bool IsInteropFailure(Exception ex)
    {
        return ex is COMException
            or InvalidCastException
            or NotSupportedException
            or PlatformNotSupportedException
            or ArgumentException;
    }

    private static int PropVariantSize => IntPtr.Size == 8 ? 24 : 16;

    private static class PropertyKeys
    {
        /// <summary>PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5.</summary>
        public static readonly PropertyKey AppUserModelId = new()
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            PropertyId = 5,
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, IntPtr pkey);

        [PreserveSig]
        int GetValue(ref PropertyKey key, IntPtr pv);

        [PreserveSig]
        int SetValue(ref PropertyKey key, IntPtr pv);

        [PreserveSig]
        int Commit();
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll")]
        internal static extern int SHGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(IntPtr pvar);
    }

    private void SetHovered(int index)
    {
        if (index == _hoveredIndex)
        {
            return;
        }

        _hoveredIndex = index;
        Invalidate();
    }

    /// <summary>
    /// Raised after the popup closed because one or more shortcuts failed to start, with one
    /// "name: reason" line per failure. The resident reports them in a tray balloon: the popup
    /// never shows modal UI, which would take activation and close (dispose) it mid-launch.
    /// </summary>
    public event Action<IReadOnlyList<string>>? LaunchFailed;

    internal void LaunchAll()
    {
        var failures = new List<string>();
        for (var i = 0; i < _group.Shortcuts.Count; i++)
        {
            if (TryLaunch(i) is { } failure)
            {
                failures.Add(failure);
            }
        }

        ReportFailures(failures);
    }

    internal void Launch(int index)
    {
        if (TryLaunch(index) is { } failure)
        {
            ReportFailures(new[] { failure });
        }
    }

    // Legacy OpenFile/ucShortcut_Click launched the process and never closed the popup
    // explicitly; the popup disappeared through OnDeactivate when the started app took focus.
    // Kept as is, so popups over apps that do not steal focus stay open. Returns the failure
    // line, or null when the shortcut started (or the index is out of range).
    private string? TryLaunch(int index)
    {
        if (index < 0 || index >= _group.Shortcuts.Count)
        {
            return null;
        }

        var entry = _group.Shortcuts[index];
        var result = ShortcutLauncher.Launch(entry);
        if (result.Started)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(entry.Name) ? entry.TargetPath : entry.Name;
        return $"{name}: {result.Error ?? "the shortcut could not be started."}";
    }

    private void ReportFailures(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var handler = LaunchFailed;
        if (!IsDisposed)
        {
            Close();
        }

        handler?.Invoke(failures);
    }

    private static int IndexForDigitKey(Keys key)
    {
        if (key is >= Keys.D1 and <= Keys.D9)
        {
            return (int)key - (int)Keys.D1;
        }

        // Legacy mapping: 0 is the tenth entry.
        return key == Keys.D0 ? 9 : -1;
    }
}
