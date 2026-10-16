using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Icons;

namespace Dockaroo.App.Editor;

/// <summary>
/// The shortcut list inside the Shortcuts card (design spec): 52px rows at a 58px pitch, each
/// with a drag grip, the target icon, an inline editable name (27 characters) and an overflow
/// menu (Rename, Move up, Move down, Remove), followed by a dashed drop area. Rows reorder by
/// dragging the grip, from the menu, or with Alt+Up/Alt+Down in the name box. The list scrolls
/// inside its card. Row icons load on a background thread (through the resident's shared
/// <see cref="IconCache"/> when one is given) and are posted back to the UI thread.
/// </summary>
internal sealed class EntryListControl : UserControl, IThemedControl
{
    /// <summary>Legacy limit: at most 20 shortcuts in one group.</summary>
    public const int MaxEntries = 20;

    private const int RowHeight = 52;
    private const int RowGap = 6;
    private const int RowIconSize = 24;
    private const int DropZoneHeight = 40;

    private readonly Panel _scroller;
    private readonly DropZone _dropZone;
    private readonly List<EntryRow> _rowControls = new();
    private readonly List<ShortcutEntry> _entries = new();
    private readonly IconCache? _icons;
    private readonly SynchronizationContext? _uiContext;
    private EntryRow? _selected;
    private ThemeTokens? _tokens;
    private ResolvedMode _mode;
    private int _dragFrom = -1;
    private int _dragTarget = -1;

    /// <summary>
    /// <paramref name="icons"/> is the resident's cache (its bitmaps stay owned by the cache);
    /// without one, each row extracts and owns its own bitmap, still off the UI thread.
    /// </summary>
    public EntryListControl(IconCache? icons = null)
    {
        _icons = icons;

        // Creating a control installs the WinForms context on this thread; icon results are
        // posted through it, which works before any handle exists.
        _uiContext = SynchronizationContext.Current;

        _scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _scroller.Resize += (_, _) => LayoutRows();
        _dropZone = new DropZone(this);
        _scroller.Controls.Add(_dropZone);
        Controls.Add(_scroller);
    }

    /// <summary>Raised whenever the selected row changes (select, reorder, remove, reset).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised after any user edit of the list: add, remove, reorder or rename.</summary>
    public event EventHandler? EntriesChanged;

    /// <summary>Raised when the drop area is clicked (the owner opens its file dialog).</summary>
    public event EventHandler? AddRequested;

    /// <summary>Raised on the UI thread when a row's icon arrives (previews redraw with it).</summary>
    public event EventHandler? IconLoaded;

    /// <summary>Entries in display order; owned by this control, callers only read (save copies).</summary>
    public IReadOnlyList<ShortcutEntry> Entries => _entries;

    /// <summary>The entry of the highlighted row, or null when nothing is selected.</summary>
    public ShortcutEntry? SelectedEntry => _selected?.Entry;

    /// <summary>Index of the highlighted row, or -1.</summary>
    public int SelectedIndex => _selected is null ? -1 : _rowControls.IndexOf(_selected);

    /// <summary>Test hook: the icon a row currently shows (null while it is still loading).</summary>
    internal Image? RowImage(int index) => _rowControls[index].Icon;

    /// <summary>Test hook: the inline name box of a row.</summary>
    internal TextBox NameBoxAt(int index) => _rowControls[index].NameBox;

    /// <summary>Test hook: the drop area's prompt text.</summary>
    internal string DropPrompt => DropZone.Prompt;

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _tokens = tokens;
        _mode = mode;
        BackColor = tokens.Surface;
        _scroller.BackColor = tokens.Surface;
        if (_scroller.IsHandleCreated)
        {
            ApplyScrollbarTheme();
        }

        foreach (var row in _rowControls)
        {
            row.ApplyTheme(tokens);
        }

        _dropZone.Invalidate();
    }

    /// <summary>Replaces every row with one row per entry; used when the editor loads a group.</summary>
    public void SetEntries(IEnumerable<ShortcutEntry> entries)
    {
        ClearRows();
        foreach (var entry in entries)
        {
            AddRow(entry);
        }

        LayoutRows();
        UpdateMoveStates();
        _scroller.AutoScrollPosition = Point.Empty;
    }

    /// <summary>
    /// Adds one entry per target path, up to <see cref="MaxEntries"/>. Returns the number of
    /// entries actually added, so the caller can report an overflow with its own error label.
    /// </summary>
    public int AddTargets(IEnumerable<string> targetPaths)
    {
        var added = 0;
        foreach (var path in targetPaths)
        {
            if (_entries.Count >= MaxEntries)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                AddRow(ShortcutEntry.Create(path));
                added++;
            }
        }

        if (added > 0)
        {
            LayoutRows();
            UpdateMoveStates();
            _scroller.ScrollControlIntoView(_dropZone);
            EntriesChanged?.Invoke(this, EventArgs.Empty);
        }

        return added;
    }

    /// <summary>Highlights the row at <paramref name="index"/> (-1 clears the selection).</summary>
    public void SelectIndex(int index)
    {
        SetSelected(index >= 0 && index < _rowControls.Count ? _rowControls[index] : null);
    }

    /// <summary>Moves an entry to a new position (menu, keyboard and grip drag all end here).</summary>
    public void MoveEntry(int from, int to)
    {
        if (from < 0 || from >= _rowControls.Count)
        {
            return;
        }

        to = Math.Clamp(to, 0, _rowControls.Count - 1);
        if (from == to)
        {
            return;
        }

        var row = _rowControls[from];
        var entry = _entries[from];
        _rowControls.RemoveAt(from);
        _entries.RemoveAt(from);
        _rowControls.Insert(to, row);
        _entries.Insert(to, entry);
        LayoutRows();
        UpdateMoveStates();
        _scroller.ScrollControlIntoView(row);
        EntriesChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes the entry at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _rowControls.Count)
        {
            return;
        }

        var row = _rowControls[index];
        _rowControls.RemoveAt(index);
        _entries.RemoveAt(index);
        _scroller.Controls.Remove(row);
        if (ReferenceEquals(_selected, row))
        {
            SetSelected(null);
        }

        row.Dispose();
        LayoutRows();
        UpdateMoveStates();
        EntriesChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyScrollbarTheme();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutRows();
    }

    private void ApplyScrollbarTheme() => NativeTheme.ApplyScrollbars(_scroller, _mode);

    private void AddRow(ShortcutEntry entry)
    {
        var row = new EntryRow(entry, this);
        if (_tokens is not null)
        {
            row.ApplyTheme(_tokens);
        }

        _rowControls.Add(row);
        _entries.Add(entry);
        _scroller.Controls.Add(row);
        BeginLoadIcon(row);
    }

    /// <summary>
    /// Rows are laid out absolutely from 96-DPI constants (a 58px pitch) so every row, whenever
    /// it was created, gets the same device-pixel geometry; the drop area follows the last row.
    /// </summary>
    private void LayoutRows()
    {
        var y = _scroller.AutoScrollPosition.Y;
        var height = LogicalToDeviceUnits(RowHeight);
        var pitch = RowPitch;

        // Decide the scrollbar up front: rows that are as wide as the client area before the
        // vertical bar appears would otherwise trigger a horizontal bar too.
        var contentHeight = (_rowControls.Count * pitch) + (_entries.Count < MaxEntries ? LogicalToDeviceUnits(DropZoneHeight) : 0);
        var scrollbar = contentHeight > _scroller.Height ? SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) : 0;
        var width = Math.Max(LogicalToDeviceUnits(200), _scroller.Width - scrollbar);
        _scroller.SuspendLayout();
        foreach (var row in _rowControls)
        {
            row.Bounds = new Rectangle(0, y, width, height);
            y += pitch;
        }

        _dropZone.Bounds = new Rectangle(0, y, width, LogicalToDeviceUnits(DropZoneHeight));
        _dropZone.Visible = _entries.Count < MaxEntries;
        _scroller.ResumeLayout(true);
    }

    /// <summary>
    /// Loads the row icon on a thread-pool thread and hands it to the row on the UI thread.
    /// A row removed (disposed) before its icon arrives just drops it.
    /// </summary>
    private void BeginLoadIcon(EntryRow row)
    {
        var source = TryCreateIconSource(row.Entry.TargetPath);
        if (source is null || _uiContext is null)
        {
            return;
        }

        var icons = _icons;
        var uiContext = _uiContext;
        var iconSize = LogicalToDeviceUnits(RowIconSize);
        Task.Run(() =>
        {
            Bitmap? bitmap;
            var owned = icons is null;
            try
            {
                bitmap = icons is not null
                    ? icons.GetOrLoad(source, iconSize).Bitmap
                    : IconExtractor.Extract(source, iconSize).Bitmap;
            }
            catch (ObjectDisposedException)
            {
                // The resident is shutting down and took the shared cache with it.
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Extraction failed for this target: the row keeps its empty icon box.
                return;
            }

            if (bitmap is not null)
            {
                uiContext.Post(_ => row.SetIcon(bitmap, owned), null);
            }
        });
    }

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

    private void SetSelected(EntryRow? row)
    {
        if (ReferenceEquals(_selected, row))
        {
            return;
        }

        _selected?.SetSelected(false);
        _selected = row;
        _selected?.SetSelected(true);
        if (row is not null)
        {
            _scroller.ScrollControlIntoView(row);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearRows()
    {
        SetSelected(null);
        foreach (var row in _rowControls)
        {
            _scroller.Controls.Remove(row);
            row.Dispose();
        }

        _rowControls.Clear();
        _entries.Clear();
    }

    private void UpdateMoveStates()
    {
        for (var i = 0; i < _rowControls.Count; i++)
        {
            _rowControls[i].SetMoveEnabled(up: i > 0, down: i < _rowControls.Count - 1);
        }
    }

    private void OnRowRenamed() => EntriesChanged?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- grip drag reorder

    private void BeginGripDrag(EntryRow row)
    {
        _dragFrom = _rowControls.IndexOf(row);
        _dragTarget = _dragFrom;
        SetSelected(row);
    }

    private void UpdateGripDrag(EntryRow row, Point rowPoint)
    {
        if (_dragFrom < 0)
        {
            return;
        }

        var inScroller = _scroller.PointToClient(row.PointToScreen(rowPoint));
        var pitch = RowPitch;
        var target = Math.Clamp((inScroller.Y - _scroller.AutoScrollPosition.Y) / Math.Max(1, pitch), 0, _rowControls.Count - 1);
        if (target != _dragTarget)
        {
            if (_dragTarget >= 0 && _dragTarget < _rowControls.Count)
            {
                _rowControls[_dragTarget].SetDropTarget(false);
            }

            _dragTarget = target;
            if (_dragTarget != _dragFrom)
            {
                _rowControls[_dragTarget].SetDropTarget(true);
            }
        }
    }

    // Same rounding as LayoutRows, so the drag target matches the drawn rows at every DPI.
    private int RowPitch => LogicalToDeviceUnits(RowHeight) + LogicalToDeviceUnits(RowGap);

    /// <summary>Capture lost mid-drag (dialog, Alt+Tab): drop the drag without moving anything.</summary>
    private void CancelGripDrag()
    {
        if (_dragTarget >= 0 && _dragTarget < _rowControls.Count)
        {
            _rowControls[_dragTarget].SetDropTarget(false);
        }

        _dragFrom = -1;
        _dragTarget = -1;
    }

    /// <summary>
    /// Keyboard on a row: Alt+Up/Alt+Down move it (name box or menu button); on the menu button,
    /// Delete removes it and the Apps key / Shift+F10 opens the menu. Returns true when handled.
    /// </summary>
    internal bool ProcessRowKey(int index, Keys keyData, bool fromNameBox)
    {
        if (index < 0 || index >= _rowControls.Count)
        {
            return false;
        }

        var keyCode = keyData & Keys.KeyCode;
        if ((keyData & Keys.Alt) == Keys.Alt && keyCode is Keys.Up or Keys.Down)
        {
            MoveEntry(index, keyCode == Keys.Up ? index - 1 : index + 1);
            return true;
        }

        if (fromNameBox)
        {
            return false;
        }

        if (keyData == Keys.Delete)
        {
            BeginInvoke(new Action(() => RemoveAt(index)));
            return true;
        }

        if (keyData == Keys.Apps || keyData == (Keys.F10 | Keys.Shift))
        {
            _rowControls[index].ShowMenu();
            return true;
        }

        return false;
    }

    private void EndGripDrag()
    {
        if (_dragFrom < 0)
        {
            return;
        }

        if (_dragTarget >= 0 && _dragTarget < _rowControls.Count)
        {
            _rowControls[_dragTarget].SetDropTarget(false);
        }

        var from = _dragFrom;
        var to = _dragTarget;
        _dragFrom = -1;
        _dragTarget = -1;
        MoveEntry(from, to);
    }

    /// <summary>One shortcut row: grip, icon, inline name and the overflow menu button.</summary>
    private sealed class EntryRow : Panel
    {
        private const int GripWidth = 20;

        private readonly EntryListControl _owner;
        private readonly PictureBox _picture;
        private readonly TextBox _name;
        private readonly ThemedButton _more;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _moveUp;
        private readonly ToolStripMenuItem _moveDown;
        private ThemeTokens? _tokens;
        private bool _selected;
        private bool _dropTarget;
        private bool _gripDragging;

        // A cache-owned bitmap is shared with the popup and must never be disposed here.
        private Bitmap? _ownedIcon;

        public EntryRow(ShortcutEntry entry, EntryListControl owner)
        {
            Entry = entry;
            _owner = owner;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // Empty until BeginLoadIcon delivers the icon.
            _picture = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom };
            _picture.Click += (_, _) => _owner.SetSelected(this);

            _name = new TextBox
            {
                Text = entry.Name,
                MaxLength = ShortcutEntry.MaxNameLength,
                BorderStyle = BorderStyle.None,
                Font = EditorFonts.Body,
                AccessibleName = "Shortcut name",
            };
            _name.TextChanged += (_, _) =>
            {
                if (Entry.Name != _name.Text)
                {
                    Entry.Name = _name.Text;
                    _owner.OnRowRenamed();
                }
            };
            _name.Enter += (_, _) => _owner.SetSelected(this);
            _name.KeyDown += (_, e) => HandleKey(e, fromNameBox: true);

            _menu = new ContextMenuStrip();
            var rename = new ToolStripMenuItem("Rename");
            rename.Click += (_, _) => BeginRename();
            _moveUp = new ToolStripMenuItem("Move up");
            _moveUp.Click += (_, _) => _owner.MoveEntry(Index, Index - 1);
            _moveDown = new ToolStripMenuItem("Move down");
            _moveDown.Click += (_, _) => _owner.MoveEntry(Index, Index + 1);
            var remove = new ToolStripMenuItem("Remove");
            remove.Click += (_, _) => _owner.BeginInvoke(new Action(() => _owner.RemoveAt(Index)));
            _menu.Items.AddRange(new ToolStripItem[] { rename, _moveUp, _moveDown, new ToolStripSeparator(), remove });

            // Tabbable so every menu action (Remove included) has a keyboard path: Tab here, then
            // Enter/Space opens the menu, Delete removes the row.
            _more = new ThemedButton(string.Empty, ButtonKind.Ghost, Glyphs.More) { AccessibleName = "More actions", TabStop = true };
            _more.Click += (_, _) => ShowMenu();
            _more.Enter += (_, _) => _owner.SetSelected(this);
            _more.KeyDown += (_, e) => HandleKey(e, fromNameBox: false);

            Controls.Add(_picture);
            Controls.Add(_name);
            Controls.Add(_more);
            ContextMenuStrip = _menu;
            PositionControls();
        }

        public ShortcutEntry Entry { get; }

        public Image? Icon => _picture.Image;

        public TextBox NameBox => _name;

        private int Index => _owner._rowControls.IndexOf(this);

        public void ApplyTheme(ThemeTokens tokens)
        {
            _tokens = tokens;
            _more.ApplyTheme(tokens, ResolvedMode.Light);
            _menu.Renderer = new ThemedMenuRenderer(tokens);
            UpdateColors();
        }

        /// <summary>UI thread: shows a loaded icon; <paramref name="owned"/> bitmaps are disposed with the row.</summary>
        public void SetIcon(Bitmap bitmap, bool owned)
        {
            if (IsDisposed)
            {
                if (owned)
                {
                    bitmap.Dispose();
                }

                return;
            }

            _picture.Image = bitmap;
            _ownedIcon?.Dispose();
            _ownedIcon = owned ? bitmap : null;
            _owner.IconLoaded?.Invoke(_owner, EventArgs.Empty);
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            UpdateColors();
        }

        public void SetDropTarget(bool target)
        {
            _dropTarget = target;
            Invalidate();
        }

        public void SetMoveEnabled(bool up, bool down)
        {
            _moveUp.Enabled = up;
            _moveDown.Enabled = down;
        }

        public void ShowMenu()
        {
            _owner.SetSelected(this);
            _menu.Show(_more, new Point(0, _more.Height));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            PositionControls();
        }

        // Rows are created at runtime, some after the form autoscaled, so a parent-driven scale
        // would hit some rows and not others. Row geometry is DPI-explicit instead.
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        {
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            PositionControls();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            PositionControls();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (e.X < LogicalToDeviceUnits(12 + GripWidth))
            {
                _gripDragging = true;
                Cursor = Cursors.SizeNS;
                _owner.BeginGripDrag(this);
            }
            else
            {
                _owner.SetSelected(this);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_gripDragging)
            {
                _owner.UpdateGripDrag(this, e.Location);
            }
            else
            {
                Cursor = e.X < LogicalToDeviceUnits(12 + GripWidth) ? Cursors.SizeNS : Cursors.Default;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_gripDragging)
            {
                _gripDragging = false;
                Cursor = Cursors.Default;
                _owner.EndGripDrag();
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_gripDragging && !Capture)
            {
                _gripDragging = false;
                Cursor = Cursors.Default;
                _owner.CancelGripDrag();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Window);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_tokens is not { } t)
            {
                return;
            }

            var border = _dropTarget ? t.Primary : _selected ? t.SelectedBorder : t.Border;
            Draw.RoundedBox(e.Graphics, ClientRectangle, LogicalToDeviceUnits(ThemedButton.Radius), RowFill(t), border, _dropTarget ? 2f : 1f);

            // Grip: two columns of three dots.
            using var dot = new SolidBrush(t.Faint);
            var size = Math.Max(2, LogicalToDeviceUnits(3));
            var left = LogicalToDeviceUnits(16);
            var top = (Height / 2) - LogicalToDeviceUnits(7);
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 2; column++)
                {
                    e.Graphics.FillEllipse(dot, left + (column * LogicalToDeviceUnits(6)), top + (row * LogicalToDeviceUnits(6)), size, size);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _picture.Image = null;
                _ownedIcon?.Dispose();
                _ownedIcon = null;
                _menu.Dispose();
            }

            base.Dispose(disposing);
        }

        private Color RowFill(ThemeTokens t) => _selected ? t.Selected : t.Surface;

        private void UpdateColors()
        {
            if (_tokens is not { } t)
            {
                return;
            }

            BackColor = RowFill(t);
            _name.BackColor = RowFill(t);
            _name.ForeColor = t.Text;
            _picture.BackColor = RowFill(t);
            Invalidate();
            _more.Invalidate();
        }

        private void BeginRename()
        {
            _name.Focus();
            _name.SelectAll();
        }

        private void HandleKey(KeyEventArgs e, bool fromNameBox)
        {
            if (_owner.ProcessRowKey(Index, e.KeyData, fromNameBox))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (fromNameBox && !IsDisposed)
                {
                    _name.Focus();
                }
            }
        }

        /// <summary>Device-pixel layout from 96-DPI constants (spec row: grip, 24px icon, name, menu).</summary>
        private void PositionControls()
        {
            if (_picture is null || _name is null || _more is null)
            {
                return;
            }

            var icon = LogicalToDeviceUnits(RowIconSize);
            var iconLeft = LogicalToDeviceUnits(44);
            var nameLeft = LogicalToDeviceUnits(84);
            var moreSize = LogicalToDeviceUnits(32);
            var right = LogicalToDeviceUnits(10);
            _picture.Bounds = new Rectangle(iconLeft, (Height - icon) / 2, icon, icon);
            _more.Bounds = new Rectangle(Width - right - moreSize, (Height - moreSize) / 2, moreSize, moreSize);
            var nameHeight = _name.PreferredHeight;
            _name.Bounds = new Rectangle(nameLeft, (Height - nameHeight) / 2, Math.Max(LogicalToDeviceUnits(60), _more.Left - nameLeft - LogicalToDeviceUnits(12)), nameHeight);
        }
    }

    /// <summary>Dashed "+ Drop apps, shortcuts, or folders here" area after the last row.</summary>
    private sealed class DropZone : Control
    {
        public const string Prompt = "Drop apps, shortcuts, or folders here";

        private readonly EntryListControl _owner;

        public DropZone(EntryListControl owner)
        {
            _owner = owner;
            Cursor = Cursors.Hand;
            AccessibleName = Prompt;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            _owner.AddRequested?.Invoke(_owner, EventArgs.Empty);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? SystemColors.Window);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_owner._tokens is not { } t)
            {
                return;
            }

            var g = e.Graphics;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Draw.RoundedRect(bounds, LogicalToDeviceUnits(ThemedButton.Radius)))
            using (var fill = new SolidBrush(t.Drop))
            using (var pen = new Pen(t.DropBorder) { DashPattern = new[] { 4f, 3f } })
            {
                var mode = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
                g.SmoothingMode = mode;
            }

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            var glyph = TextRenderer.MeasureText(g, Glyphs.Add, EditorFonts.IconSmall, Size.Empty, flags);
            var text = TextRenderer.MeasureText(g, Prompt, EditorFonts.Label, Size.Empty, flags);
            var gap = LogicalToDeviceUnits(8);
            var x = (Width - glyph.Width - gap - text.Width) / 2;
            TextRenderer.DrawText(g, Glyphs.Add, EditorFonts.IconSmall, new Rectangle(x, 0, glyph.Width, Height), t.Muted, flags);
            TextRenderer.DrawText(g, Prompt, EditorFonts.Label, new Rectangle(x + glyph.Width + gap, 0, text.Width, Height), t.Muted, flags);
        }
    }
}
