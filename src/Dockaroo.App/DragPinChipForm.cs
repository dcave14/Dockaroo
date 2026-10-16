using System.Drawing;
using System.Windows.Forms;
using Dockaroo.Core;

namespace Dockaroo.App;

/// <summary>
/// Drag source for the human pin gesture (F-021: the app never pins silently and never
/// injects input). Shows the shortcut's icon, the group name and one instruction; pressing
/// and moving the left mouse button starts DoDragDrop with the .lnk as CF_HDROP, so dragging
/// the chip onto the taskbar pins it - the user moves the mouse. The chip closes when the
/// drag ends or on its X, and is owned by the editor, so it closes with it.
/// </summary>
internal sealed class DragPinChipForm : Form
{
    /// <summary>The one instruction line shown on the chip.</summary>
    internal const string InstructionText = "Drag onto the taskbar to pin";

    private readonly string _shortcutPath;
    private readonly Icon? _shortcutIcon;
    private readonly Font _titleFont;
    private bool _dragArmed;
    private bool _dragStarted;

    /// <summary>
    /// Creates the chip. <paramref name="shortcutIcon"/> is the icon of the created .lnk, so
    /// what is dragged matches what lands on the taskbar; the chip takes ownership of it.
    /// </summary>
    public DragPinChipForm(string groupName, string shortcutPath, Icon? shortcutIcon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);

        _shortcutPath = shortcutPath;
        _shortcutIcon = shortcutIcon;

        Text = AppConstants.AppName;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        // Laid out in 96-DPI pixels; PerMonitorV2 scales the chip and its labels per monitor.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(280, 72);
        BackColor = SystemColors.Control;

        _titleFont = new Font(Font, FontStyle.Bold);
        TitleLabel = new Label
        {
            Text = groupName,
            AutoEllipsis = true,
            Font = _titleFont,
            Bounds = new Rectangle(54, 12, 190, 20),
        };
        InstructionLabel = new Label
        {
            Text = InstructionText,
            AutoEllipsis = true,
            Bounds = new Rectangle(54, 34, 214, 18),
        };
        CloseLabel = new Label
        {
            Text = "✕",
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(ClientSize.Width - 26, 6, 20, 20),
            Cursor = Cursors.Hand,
            AccessibleName = "Close",
        };
        CloseLabel.Click += (_, _) => Close();

        Controls.Add(TitleLabel);
        Controls.Add(InstructionLabel);
        Controls.Add(CloseLabel);

        // The labels cover most of the chip; a drag started on them must work like a drag
        // started on the form itself.
        TitleLabel.MouseDown += OnChipMouseDown;
        TitleLabel.MouseMove += OnChipMouseMove;
        InstructionLabel.MouseDown += OnChipMouseDown;
        InstructionLabel.MouseMove += OnChipMouseMove;
    }

    /// <summary>The group name shown as the chip title (asserted by tests).</summary>
    internal Label TitleLabel { get; }

    /// <summary>The instruction line (asserted by tests).</summary>
    internal Label InstructionLabel { get; }

    private Label CloseLabel { get; }

    /// <summary>The .lnk the drag payload carries.</summary>
    internal string ShortcutPath => _shortcutPath;

    /// <summary>
    /// The drag payload: exactly the shortcut path as CF_HDROP, which is what the taskbar
    /// consumes when the user drops the chip on it.
    /// </summary>
    internal static DataObject CreateDragData(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { shortcutPath });
        return data;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        OnChipMouseDown(this, e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        OnChipMouseMove(this, e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragArmed = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_shortcutIcon is not null)
        {
            // Painted, so not autoscaled: convert the 96-DPI box to this window's DPI.
            e.Graphics.DrawIcon(_shortcutIcon, new Rectangle(
                LogicalToDeviceUnits(12),
                LogicalToDeviceUnits(20),
                LogicalToDeviceUnits(32),
                LogicalToDeviceUnits(32)));
        }

        ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.ControlDark, ButtonBorderStyle.Solid);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shortcutIcon?.Dispose();
            _titleFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnChipMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragArmed = true;
        }
    }

    private void OnChipMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragArmed || _dragStarted || (e.Button & MouseButtons.Left) == 0)
        {
            return;
        }

        _dragStarted = true;
        _dragArmed = false;
        DoDragDrop(CreateDragData(_shortcutPath), DragDropEffects.Copy);

        // The drag ended (dropped or cancelled): the chip's job is done either way.
        Close();
    }
}
