using System.Drawing;
using System.Windows.Forms;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Controls;

/// <summary>
/// In-editor drag source for the human pin gesture (Windows 11 allows no programmatic pinning).
/// A small card shown above the footer after "Create taskbar shortcut": the shortcut's icon,
/// the group name, one instruction, a "Show in File Explorer" link and a close button. Pressing
/// and moving the left mouse button on the card drags the .lnk exactly like File Explorer does;
/// the card hides when the drag ends.
/// </summary>
internal sealed class PinChipCard : Control, IThemedControl
{
    /// <summary>The one instruction line shown on the card.</summary>
    internal const string InstructionText = "Drag onto the taskbar to pin";

    /// <summary>Text of the link that opens the shortcut's folder with the shortcut selected.</summary>
    internal const string ShowInFolderText = "Show in File Explorer";

    /// <summary>Card height in 96-DPI pixels; the width is the button's it sits on.</summary>
    internal const int LogicalHeight = 132;

    // Stacked, centred layout in 96-DPI pixels: icon, name, instruction, link.
    private const int IconSize = 40;
    private const int IconTop = 12;
    private const int TitleTop = 58;
    private const int InstructionTop = 80;
    private const int LinkTop = 102;

    private const int Radius = 12;

    private Icon? _icon;
    private string _title = string.Empty;
    private string? _shortcutPath;
    private bool _dragArmed;
    private Color _border = SystemColors.ControlDark;
    private Color _text = SystemColors.ControlText;
    private Color _muted = SystemColors.GrayText;

    public PinChipCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Visible = false;
        Cursor = Cursors.Hand;
        AccessibleName = "Drag onto the taskbar to pin";

        ShowInFolderLink = new LinkLabel
        {
            Text = ShowInFolderText,
            AutoSize = true,
            Font = EditorFonts.Label,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        ShowInFolderLink.LinkClicked += (_, _) =>
        {
            if (_shortcutPath is not null)
            {
                ShellHandoff.OpenInExplorer(ShellHandoff.ExplorerSelectArguments(_shortcutPath));
            }
        };

        CloseButton = new Label
        {
            Text = "✕",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = EditorFonts.Label,
            Cursor = Cursors.Hand,
            AccessibleName = "Close",
        };
        CloseButton.Click += (_, _) => Dismiss();

        Controls.Add(ShowInFolderLink);
        Controls.Add(CloseButton);
    }

    /// <summary>The "Show in File Explorer" link (asserted by tests).</summary>
    internal LinkLabel ShowInFolderLink { get; }

    /// <summary>The close button (asserted by tests).</summary>
    internal Label CloseButton { get; }

    /// <summary>The group name shown as the card title.</summary>
    internal string Title => _title;

    /// <summary>The .lnk the drag carries; null while the card is dismissed.</summary>
    internal string? ShortcutPath => _shortcutPath;

    /// <summary>True between <see cref="ShowFor"/> and <see cref="Dismiss"/> (Visible reads false on unshown forms).</summary>
    internal bool IsShowing => _shortcutPath is not null;

    /// <summary>Shows the card for one shortcut; takes ownership of <paramref name="icon"/>.</summary>
    public void ShowFor(string groupName, string shortcutPath, Icon? icon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        _icon?.Dispose();
        _icon = icon;
        _title = groupName;
        _shortcutPath = shortcutPath;
        Visible = true;
        BringToFront();
        Invalidate();
    }

    /// <summary>Hides the card and releases its icon; safe to call when already hidden.</summary>
    public void Dismiss()
    {
        _dragArmed = false;
        _shortcutPath = null;
        _icon?.Dispose();
        _icon = null;
        Visible = false;
    }

    public void ApplyTheme(ThemeTokens tokens, ResolvedMode mode)
    {
        _border = tokens.SelectedBorder;
        _text = tokens.Text;
        _muted = tokens.Muted;
        BackColor = tokens.Surface;
        ShowInFolderLink.BackColor = tokens.Surface;
        ShowInFolderLink.LinkColor = tokens.Primary;
        ShowInFolderLink.ActiveLinkColor = tokens.PrimaryHover;
        ShowInFolderLink.VisitedLinkColor = tokens.Primary;
        CloseButton.BackColor = tokens.Surface;
        CloseButton.ForeColor = tokens.Muted;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        CloseButton.Bounds = new Rectangle(Width - LogicalToDeviceUnits(26), LogicalToDeviceUnits(6), LogicalToDeviceUnits(20), LogicalToDeviceUnits(20));
        ShowInFolderLink.Location = new Point((Width - ShowInFolderLink.Width) / 2, LogicalToDeviceUnits(LinkTop));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);

        // The card floats over other cards and the footer, so no single colour can fill its
        // corners: clip the window to the rounded shape and let whatever is behind show through.
        // WinForms disposes the previous Region when a new one replaces it.
        // One pixel wider than the client area so the anti-aliased right and bottom border survive the clip.
        using (var path = Draw.RoundedRect(new RectangleF(0, 0, Width + 1, Height + 1), LogicalToDeviceUnits(Radius) + 1))
        {
            Region = new Region(path);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Inside the rounded region only; the surface is painted by OnPaint.
        e.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw.RoundedBox(e.Graphics, ClientRectangle, LogicalToDeviceUnits(Radius), BackColor, _border);
        var iconSize = LogicalToDeviceUnits(IconSize);
        if (_icon is not null)
        {
            e.Graphics.DrawIcon(_icon, new Rectangle((Width - iconSize) / 2, LogicalToDeviceUnits(IconTop), iconSize, iconSize));
        }

        var pad = LogicalToDeviceUnits(12);
        var textWidth = Width - (2 * pad);
        const TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, _title, EditorFonts.BodyStrong, new Rectangle(pad, LogicalToDeviceUnits(TitleTop), textWidth, LogicalToDeviceUnits(22)), _text, flags);
        TextRenderer.DrawText(e.Graphics, InstructionText, EditorFonts.Label, new Rectangle(pad, LogicalToDeviceUnits(InstructionTop), textWidth, LogicalToDeviceUnits(18)), _muted, flags);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragArmed = true;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragArmed = false;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragArmed || _shortcutPath is null || (e.Button & MouseButtons.Left) == 0)
        {
            return;
        }

        _dragArmed = false;

        // Copy | Link like an Explorer drag: the taskbar pins through the Link effect. Move is
        // left out so no drop target can take the .lnk away from the data root.
        DoDragDrop(ShellHandoff.CreateShellDragData(_shortcutPath), DragDropEffects.Copy | DragDropEffects.Link);

        // The drag ended (dropped or cancelled): the card's job is done either way.
        Dismiss();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon?.Dispose();
            _icon = null;
            Region?.Dispose();
        }

        base.Dispose(disposing);
    }
}
