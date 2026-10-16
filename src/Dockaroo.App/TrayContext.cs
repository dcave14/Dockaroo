using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using Dockaroo.App.Appearance;
using Dockaroo.App.Editor;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Popup;

namespace Dockaroo.App;

/// <summary>
/// ApplicationContext for the resident process. <see cref="Preload"/> loads every group and the
/// icons the popup needs into memory so showing the popup reads memory only. Program calls it
/// after the pipe server is already serving, so requests that arrive mid-preload are held by
/// <see cref="StartupPopupQueue"/> and shown once the load finishes. A group that appears later
/// (cold group) is loaded and icon-preloaded on a background thread and its popup opens when
/// that finishes - never on the UI thread (see <see cref="BeginColdLoad"/>). The
/// notification-area icon keeps the message loop alive; its menu can show the popup for a
/// preloaded group, open the group editor ("New group..." / "Edit groups...") and exits the
/// resident instance. An editor save or delete refreshes the local group list and icons through
/// <see cref="OnGroupsChanged"/>, so the next popup shows the new state without a restart and
/// without adding IO to the show path.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly Control _marshal;
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _trayIcon;
    private readonly GroupStore _store;
    private readonly IconCache _icons;
    private readonly PinnedStateCache _pins;
    private readonly ThemeService _theme;

    // Menu images for the palette items; ToolStripItem.Dispose does not dispose its Image.
    private readonly Dictionary<string, Bitmap> _paletteMenuImages = new(StringComparer.Ordinal);
    private bool _menuDirty;
    private readonly List<Group> _loadedGroups;
    private readonly HashSet<string> _coldLoads = new(StringComparer.OrdinalIgnoreCase);

    // Coalesces display-change bursts (a monitor plug-in raises several) into one warm-up.
    private readonly System.Windows.Forms.Timer _displayChangeDebounce = new() { Interval = 500 };
    private PopupForm? _popup;
    private EditorForm? _editor;

    public TrayContext()
    {
        var dataRoot = DataRootResolver.ForCurrentProcess();
        _store = new GroupStore(dataRoot.ConfigDirectory);
        _icons = new IconCache(dataRoot.IconCacheDirectory);

        // Pin state is decided off the show path (startup and background refreshes); the popup
        // reads it from memory only.
        _pins = new PinnedStateCache(
            dataRoot.ShortcutsDirectory,
            PinnedStateCache.DefaultPinnedDirectory(),
            Application.ExecutablePath);

        _loadedGroups = new List<Group>();

        // Hidden control so payload callbacks from the pipe thread can reach the UI thread.
        _marshal = new Control();
        _ = _marshal.Handle;

        // App appearance (mode + palette logo). Created after _marshal so it captures the UI
        // thread's WinForms context for Windows colour-mode notifications.
        _theme = new ThemeService(
            new AppearanceStore(dataRoot.AppearanceFile),
            postToUi: action => _marshal.BeginInvoke(action));
        _theme.Changed += (_, _) => OnThemeChanged();

        _menu = new ContextMenuStrip();

        // Opening the tray menu is a cheap moment to re-check pin state in the background.
        _menu.Opening += (_, _) => RefreshPinsInBackground();

        // A rebuild requested while the menu is open waits until it closes (it disposes items).
        _menu.Closed += (_, _) =>
        {
            if (_menuDirty && !_marshal.IsDisposed)
            {
                _marshal.BeginInvoke(new Action(BuildMenu));
            }
        };
        BuildMenu();

        _trayIcon = new NotifyIcon
        {
            // The selected palette's logo (mint = the exe's own icon by default), small frame.
            Icon = _theme.GetAppIcon(SystemInformation.SmallIconSize),
            Text = AppConstants.AppName,
            Visible = true,
            ContextMenuStrip = _menu,
        };

        // A monitor added or rescaled needs icons at its size before its first popup.
        _displayChangeDebounce.Tick += (_, _) => WarmIconsForDisplays();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>
    /// Test-only hook: when set, <see cref="Preload"/> invokes it before loading anything, so a
    /// harness can hold the startup preload open and watch how requests are queued. Null in
    /// production; the unit tests drive <see cref="StartupPopupQueue"/> directly instead.
    /// </summary>
    internal static Action? PreloadDelayHook { get; set; }

    /// <summary>
    /// Loads every group and preloads the icons the popup needs. Program calls this after the
    /// pipe server already accepts requests: a request that arrives while this runs is queued
    /// by <see cref="StartupPopupQueue"/> and shown once it returns, never dropped. The menu is
    /// rebuilt here because it is created before the groups are known.
    /// </summary>
    public void Preload()
    {
        PreloadDelayHook?.Invoke();

        _loadedGroups.Clear();
        _loadedGroups.AddRange(_store.LoadAll());
        PreloadIcons(_icons, IconSources(_loadedGroups).ToList(), MonitorDpi.PopupIconSizes());
        _pins.Refresh(_loadedGroups);
        BuildMenu();
    }

    /// <summary>Shows the popup for a request on the UI thread; safe to call from any thread.</summary>
    public void PostShowPopup(PopupRequest request)
    {
        if (_marshal.IsDisposed || !_marshal.IsHandleCreated)
        {
            return;
        }

        void Show()
        {
            ShowPopup(request);
        }

        if (_marshal.InvokeRequired)
        {
            _marshal.BeginInvoke(new Action(Show));
        }
        else
        {
            Show();
        }
    }

    private void BuildMenu()
    {
        if (_menu.Visible)
        {
            _menuDirty = true;
            return;
        }

        _menuDirty = false;

        // Clear() alone leaks the old items and their handlers (backlog P2-11).
        var oldItems = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var item in oldItems)
        {
            item.Dispose();
        }

        var show = new ToolStripMenuItem("Show popup");
        if (_loadedGroups.Count == 0)
        {
            show.Enabled = false;
        }
        else if (_loadedGroups.Count == 1)
        {
            show.Click += (_, _) => ShowPopupForGroup(_loadedGroups[0].Name);
        }
        else
        {
            // Simple picker: one submenu entry per preloaded group.
            foreach (var group in _loadedGroups)
            {
                var name = group.Name;
                show.DropDownItems.Add(name, null, (_, _) => ShowPopupForGroup(name));
            }
        }

        _menu.Items.Add(show);

        _menu.Items.Add("New group...", null, (_, _) => OpenEditor(listGroups: false));

        var editGroups = new ToolStripMenuItem("Edit groups...");
        if (_loadedGroups.Count == 0)
        {
            editGroups.Enabled = false;
        }
        else
        {
            editGroups.Click += (_, _) => OpenEditor(listGroups: true);
        }

        _menu.Items.Add(editGroups);
        _menu.Items.Add(BuildAppearanceMenu());
        _menu.Items.Add("Exit", null, (_, _) => Exit());
    }

    /// <summary>
    /// Interim Appearance picker (until the editor's Appearance page lands): app mode and the
    /// five logo palettes, each with a check mark on the current choice and its own logo.
    /// </summary>
    private ToolStripMenuItem BuildAppearanceMenu()
    {
        var appearance = new ToolStripMenuItem("Appearance");
        var current = _theme.Preferences;
        foreach (var (mode, label) in new[] { (AppMode.Light, "Light"), (AppMode.Dark, "Dark"), (AppMode.System, "System") })
        {
            var item = new ToolStripMenuItem(label) { Checked = current.Mode == mode };
            item.Click += (_, _) => ApplyAppearance(() => _theme.SetMode(mode));
            appearance.DropDownItems.Add(item);
        }

        appearance.DropDownItems.Add(new ToolStripSeparator());
        foreach (var palette in ThemeCatalog.Default.Palettes)
        {
            var id = palette.Id;
            var item = new ToolStripMenuItem(TrayMessages.MenuLabel(palette.Name))
            {
                Checked = current.PaletteId == id,
                Image = PaletteMenuImage(id),
            };
            item.Click += (_, _) => ApplyAppearance(() => _theme.SetPalette(id));
            appearance.DropDownItems.Add(item);
        }

        return appearance;
    }

    private Bitmap PaletteMenuImage(string paletteId)
    {
        if (!_paletteMenuImages.TryGetValue(paletteId, out var image))
        {
            image = _theme.GetIcon(paletteId, SystemInformation.SmallIconSize).ToBitmap();
            _paletteMenuImages[paletteId] = image;
        }

        return image;
    }

    private void ApplyAppearance(Func<string?> change)
    {
        if (change() is { } error)
        {
            Notify(error);
        }
    }

    /// <summary>
    /// Palette or mode changed: the tray and an open editor take the new logo. The menu is
    /// rebuilt after the current click finishes, never from inside the item's own Click.
    /// </summary>
    private void OnThemeChanged()
    {
        // An open editor follows ThemeService.Changed itself (icon, colours, title bar).
        _trayIcon.Icon = _theme.GetAppIcon(SystemInformation.SmallIconSize);

        if (!_marshal.IsDisposed && _marshal.IsHandleCreated)
        {
            _marshal.BeginInvoke(new Action(BuildMenu));
        }
    }

    /// <summary>
    /// Opens the group editor, or brings the already open editor to the front. The editor
    /// raises <see cref="EditorForm.GroupsChanged"/> after a save or delete; the very next
    /// popup then serves the refreshed groups from memory.
    /// </summary>
    private void OpenEditor(bool listGroups)
    {
        if (_editor is { IsDisposed: false })
        {
            _editor.Activate();
            return;
        }

        var editor = new EditorForm(_store, listGroups, icons: _icons, theme: _theme);
        editor.GroupsChanged += (_, _) => OnGroupsChanged();
        editor.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_editor, editor))
            {
                _editor = null;
            }
        };

        _editor = editor;
        editor.Show();
        editor.Activate();
    }

    /// <summary>
    /// Picks up editor changes without restarting: the store cache already holds the saved or
    /// deleted state, so the local group list is rebuilt from it, the menu is refreshed and
    /// the icons are warmed on a background thread so extraction never blocks the UI.
    /// </summary>
    private void OnGroupsChanged()
    {
        _loadedGroups.Clear();
        _loadedGroups.AddRange(_store.LoadAll());
        BuildMenu();

        var sources = IconSources(_loadedGroups).ToList();
        var groups = _loadedGroups.ToArray();
        var sizes = MonitorDpi.PopupIconSizes();
        Task.Run(() =>
        {
            try
            {
                PreloadIcons(_icons, sources, sizes);
                _pins.Refresh(groups);
            }
            catch (ObjectDisposedException)
            {
                // The resident is shutting down; the icon cache is already gone.
            }
        });
    }

    /// <summary>
    /// Re-checks pin state on a background thread: the pinned-folder scan, the shell probe and
    /// the in-place .lnk repairs are IO and never run on the popup show path.
    /// </summary>
    private void RefreshPinsInBackground()
    {
        var groups = _loadedGroups.ToArray();
        Task.Run(() => _pins.Refresh(groups));
    }

    private void ShowPopupForGroup(string? groupName)
    {
        // Tray clicks have no captured payload position; use the mouse position right now. The
        // tray icon sits on the taskbar, so the popup is flush there too (no button hit test on
        // the UI thread; it centres on the cursor).
        var cursor = Cursor.Position;
        ShowPopup(new PopupRequest(cursor, groupName, HasCursor: true) { Taskbar = TaskbarLocator.Locate(cursor, findButton: false) });
    }

    private void ShowPopup(PopupRequest request)
    {
        if (request.OpenEditor)
        {
            OpenEditor(listGroups: true);
            return;
        }

        var group = ResolveGroup(request.GroupName, request);
        if (group is not null)
        {
            ShowPopupCore(group, request);
        }
    }

    private void ShowPopupCore(Group group, PopupRequest request)
    {
        if (group.Shortcuts.Count == 0)
        {
            return;
        }

        var anchor = request.HasCursor ? request.Cursor : Cursor.Position;

        // Memory-only: the AppUserModelID was derived and cached by the last off-show-path
        // pin refresh. Null keeps the legacy tool-window form (no matching pin to merge with).
        _pins.TryGetPinnedAppUserModelId(group.Name, out var pinnedAppUserModelId);

        _popup?.Dispose();
        _popup = new PopupForm(group, _icons, anchor, pinnedAppUserModelId, taskbar: request.Taskbar);
        _popup.LaunchFailed += failures => Notify(TrayMessages.LaunchFailures(failures));
        _popup.Show();
        _popup.Activate();

        // Activate() raises the window again without a new Activated event: tuck it back below
        // the clicked taskbar.
        _popup.TuckBelowTaskbar();
    }

    /// <summary>
    /// Memory-only lookup for the show path. A preloaded group comes back from the store cache;
    /// a cache miss starts a cold load instead of touching disk on the UI thread, and returns
    /// null so the caller does not wait.
    /// </summary>
    private Group? ResolveGroup(string? name, PopupRequest request)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            // Legacy behavior: no name means the first configured group.
            return _loadedGroups.Count > 0 ? _loadedGroups[0] : null;
        }

        if (_store.TryGetCached(name, out var cached) && cached is not null)
        {
            return cached;
        }

        BeginColdLoad(name, request);
        return null;
    }

    /// <summary>
    /// COLD-GROUP EXCEPTION to the zero-IO show contract: a group that was not preloaded at
    /// startup (created while the resident is running) has no memory copy, so its group load
    /// and icon preload run on a background thread - never on the UI thread - and the popup
    /// opens when they finish, marshalled back through <see cref="_marshal"/>. Preloaded groups
    /// never take this path, so their show/paint stays memory-only. Requests that arrive for the
    /// same group while it is loading join the in-flight load.
    /// </summary>
    private void BeginColdLoad(string name, PopupRequest request)
    {
        if (!_coldLoads.Add(name))
        {
            return;
        }

        var sizes = MonitorDpi.PopupIconSizes();
        Task.Run(() =>
        {
            var result = _store.Load(name);
            if (result.Status == GroupLoadStatus.Loaded && result.Group is not null)
            {
                PreloadIcons(_icons, IconSources(new[] { result.Group }).ToList(), sizes);
            }

            if (_marshal.IsDisposed || !_marshal.IsHandleCreated)
            {
                return;
            }

            _marshal.BeginInvoke(new Action(() => CompleteColdLoad(name, result, request)));
        });
    }

    private void CompleteColdLoad(string name, GroupLoadResult result, PopupRequest request)
    {
        _coldLoads.Remove(name);
        if (result.Status != GroupLoadStatus.Loaded || result.Group is null)
        {
            // Typically a pinned button whose group was renamed or deleted: say so instead of
            // silently doing nothing.
            if (TrayMessages.ForFailedLoad(name, result.Status) is { } message)
            {
                Notify(message);
            }

            return;
        }

        if (!_loadedGroups.Contains(result.Group))
        {
            _loadedGroups.Add(result.Group);
        }

        ShowPopupCore(result.Group, request);
    }

    /// <summary>
    /// Display change (monitor added, scale changed). May arrive on the SystemEvents thread, so
    /// it only (re)starts the UI-thread debounce; <see cref="WarmIconsForDisplays"/> reads the
    /// UI-owned group list there.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_marshal.IsDisposed || !_marshal.IsHandleCreated)
        {
            return;
        }

        void Restart()
        {
            _displayChangeDebounce.Stop();
            _displayChangeDebounce.Start();
        }

        try
        {
            if (_marshal.InvokeRequired)
            {
                _marshal.BeginInvoke(new Action(Restart));
            }
            else
            {
                Restart();
            }
        }
        catch (InvalidOperationException)
        {
            // The marshal handle went away between the check and the post: shutting down.
        }
    }

    /// <summary>
    /// UI thread, once per display-change burst: snapshot the groups and warm the icon sizes the
    /// new monitor layout needs on a background thread. Until it finishes the popup draws the
    /// 100% icons scaled.
    /// </summary>
    private void WarmIconsForDisplays()
    {
        _displayChangeDebounce.Stop();
        var groups = _loadedGroups.ToArray();
        var sizes = MonitorDpi.PopupIconSizes();
        Task.Run(() =>
        {
            try
            {
                PreloadIcons(_icons, IconSources(groups).ToList(), sizes);
            }
            catch (ObjectDisposedException)
            {
                // The resident is shutting down; the icon cache is already gone.
            }
        });
    }

    /// <summary>Preloads every source at every popup icon size (one size per distinct monitor DPI).</summary>
    private static void PreloadIcons(IconCache icons, IReadOnlyList<IconSource> sources, IReadOnlyCollection<int> sizes)
    {
        foreach (var size in sizes)
        {
            icons.Preload(sources, size);
        }
    }

    private static IEnumerable<IconSource> IconSources(IEnumerable<Group> groups)
    {
        foreach (var group in groups)
        {
            foreach (var entry in group.Shortcuts)
            {
                var source = TryCreateIconSource(entry.TargetPath);
                if (source is not null)
                {
                    yield return source;
                }
            }
        }
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

    /// <summary>Non-modal report through the tray icon (no dialog that could steal focus).</summary>
    private void Notify(string text)
    {
        if (_trayIcon.Visible && text.Length > 0)
        {
            _trayIcon.ShowBalloonTip(5000, AppConstants.AppName, text, ToolTipIcon.Warning);
        }
    }

    private void Exit()
    {
        // Unsaved editor changes get the Save / Don't save / Cancel prompt; Cancel keeps running.
        if (_editor is { IsDisposed: false } editor && !editor.TryCloseForExit())
        {
            return;
        }

        _trayIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _displayChangeDebounce.Dispose();
            _editor?.Dispose();
            _popup?.Dispose();
            _icons.Dispose();
            _trayIcon.Dispose();
            _theme.Dispose();
            foreach (var image in _paletteMenuImages.Values)
            {
                image.Dispose();
            }
            _menu.Dispose();
            _marshal.Dispose();
        }

        base.Dispose(disposing);
    }
}
