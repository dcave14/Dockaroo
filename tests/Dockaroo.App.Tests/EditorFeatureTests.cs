using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.App.Editor;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Dockaroo.Core.Popup;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// task-016: popup transparency rule (P2-6), the taskbar preview, group "Change icon" and the
/// Appearance page. Forms are created, never shown; all data lives in a temp root.
/// </summary>
public sealed class EditorFeatureTests : IDisposable
{
    private readonly string _root;
    private readonly string _config;
    private readonly string _shortcuts;

    public EditorFeatureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        _config = Path.Combine(_root, "config");
        _shortcuts = Path.Combine(_root, "Shortcuts");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_shortcuts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a leftover folder is harmless.
        }
    }

    // ---------------------------------------------------------------- P2-6 transparency rule

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(10, 0.9)]
    [InlineData(35, 0.65)]
    [InlineData(90, 0.1)]
    [InlineData(100, 0.1)]
    [InlineData(-5, 1.0)]
    public void OpacityFor_NeverRendersPast90PercentTransparent(int transparency, double opacity)
    {
        Assert.Equal(opacity, PopupPainter.OpacityFor(transparency), 6);
    }

    [Fact]
    public void Popup_WithAStored100_IsStillVisible()
    {
        RunOnStaThread(() =>
        {
            var group = new Group { Name = "Ghost", Opacity = 100 };
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Notepad(), Name = "notepad" });
            using var icons = new IconCache(Path.Combine(_root, "icons"));

            using var popup = new PopupForm(group, icons, new Point(10, 10), metrics: PopupMetrics.Default);
            using var frame = popup.RenderLayer();

            // The window itself is never faded; the surface is clamped to 90% transparent.
            Assert.Equal(1.0, popup.Opacity, 2);
            Assert.Equal(25, frame.GetPixel(3, 3).A);
        });
    }

    [Theory]
    [InlineData(50, "#1F1F1F", 128)]
    [InlineData(10, "#2A6FDB", 230)]
    [InlineData(0, "#F3F3F3", 255)]
    public void Popup_TransparencyFadesTheSurface_NotTheIcons(int transparency, string color, int surfaceAlpha)
    {
        RunOnStaThread(() =>
        {
            var group = new Group { Name = "Glass", Opacity = transparency, ColorHex = color };
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Notepad(), Name = "notepad" });
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Path.Combine(_root, "missing.exe"), Name = "zeta" });
            using var icons = new IconCache(Path.Combine(_root, "icons"));
            icons.Preload(new[] { IconSource.FromPath(Notepad()) }, PopupMetrics.Default.IconSize);

            using var popup = new PopupForm(group, icons, new Point(10, 10), metrics: PopupMetrics.Default);
            using var frame = popup.RenderLayer();

            // Surface: inside the popup, outside every plate.
            Assert.Equal(surfaceAlpha, frame.GetPixel(3, 3).A);
            Assert.Equal(surfaceAlpha, frame.GetPixel(frame.Width - 4, frame.Height - 4).A);

            // Icon: the notepad icon's opaque middle is untouched by the transparency.
            var cell = Dockaroo.Core.Popup.PopupGeometry.TileBounds(0, group.Width, PopupMetrics.Default);
            var middle = new Point(cell.X + (cell.Width / 2), cell.Y + (cell.Height / 2));
            Assert.Equal(255, frame.GetPixel(middle.X, middle.Y).A);

            // A missing icon's placeholder letter is drawn opaque, not punched out of the surface.
            var placeholder = Dockaroo.Core.Popup.PopupGeometry.TileBounds(1, group.Width, PopupMetrics.Default);
            var iconMargin = (PopupMetrics.Default.PlateSize - PopupMetrics.Default.IconSize) / 2;
            var fill = frame.GetPixel(placeholder.X + iconMargin + 1, placeholder.Y + iconMargin + 1);
            var letterPixels = 0;
            for (var y = placeholder.Top; y < placeholder.Bottom; y++)
            {
                for (var x = placeholder.Left; x < placeholder.Right; x++)
                {
                    // Letter ink: clearly unlike the placeholder fill, and at least as opaque
                    // (GDI text would have left alpha-0 holes instead).
                    var pixel = frame.GetPixel(x, y);
                    var diff = Math.Abs(pixel.R - fill.R) + Math.Abs(pixel.G - fill.G) + Math.Abs(pixel.B - fill.B);
                    if (diff > 90 && pixel.A >= fill.A)
                    {
                        letterPixels++;
                    }
                }
            }

            Assert.True(letterPixels > 10, $"placeholder letter drew only {letterPixels} pixels");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public void Popup_IsALayeredWindow_AndItsFrameReachesIt(int transparency)
    {
        RunOnStaThread(() =>
        {
            var group = new Group { Name = "Layered", Opacity = transparency };
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Notepad(), Name = "notepad" });
            using var icons = new IconCache(Path.Combine(_root, "icons"));

            using var popup = new PopupForm(group, icons, new Point(10, 10), metrics: PopupMetrics.Default);
            _ = popup.Handle;

            const int GwlExStyle = -20;
            const long WsExLayered = 0x00080000;
            Assert.NotEqual(0, GetWindowLongPtr(popup.Handle, GwlExStyle).ToInt64() & WsExLayered);
            Assert.True(popup.LayerUpToDate, "UpdateLayeredWindow did not accept the first frame");
            // Nothing re-routes the window through SetLayeredWindowAttributes (that breaks ULW).
            Assert.Equal(1.0, popup.Opacity, 2);
        });
    }

    [Fact]
    public void Popup_TranslucentTrim_IsValidPremultipliedAlpha()
    {
        RunOnStaThread(() =>
        {
            var group = new Group { Name = "Trim", Opacity = 10 };
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Notepad(), Name = "notepad" });
            using var icons = new IconCache(Path.Combine(_root, "icons"));
            using var popup = new PopupForm(group, icons, new Point(10, 10), metrics: PopupMetrics.Default);
            using var frame = popup.RenderLayer();

            // Raw bytes (GetPixel un-premultiplies and would hide a bad premultiply): every
            // channel at most alpha, and the trim carries the surface alpha, not fill + trim.
            var data = frame.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                foreach (var (x, y) in new[] { (0, 5), (5, 0), (frame.Width - 1, 5), (5, 5) })
                {
                    var offset = (y * data.Stride) + (x * 4);
                    var b = Marshal.ReadByte(data.Scan0, offset);
                    var g = Marshal.ReadByte(data.Scan0, offset + 1);
                    var r = Marshal.ReadByte(data.Scan0, offset + 2);
                    var a = Marshal.ReadByte(data.Scan0, offset + 3);
                    Assert.True(r <= a && g <= a && b <= a, $"({x},{y}) is not premultiplied: {a},{r},{g},{b}");
                    Assert.InRange(a, 228, 231);
                }
            }
            finally
            {
                frame.UnlockBits(data);
            }
        });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    // ---------------------------------------------------------------- preview

    [Theory]
    [InlineData(0, 6, "No shortcuts yet")]
    [InlineData(1, 6, "1 app · 1 row")]
    [InlineData(4, 4, "4 apps · 1 row")]
    [InlineData(7, 3, "7 apps · 3 rows")]
    public void PreviewCaption_CountsAppsAndRows(int count, int width, string caption)
    {
        Assert.Equal(caption, TaskbarPreview.Caption(count, width));
    }

    [Fact]
    public void Preview_FollowsTheEditBufferAndDrawsThePopupAboveTheTaskbar()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", entries: 3);
            using var editor = CreateEditor(store);
            var preview = Get<TaskbarPreview>(editor, "_preview");
            preview.Size = new Size(384, 264);

            Assert.Equal("3 apps · 1 row", Get<Label>(editor, "_previewCaption").Text);

            Get<Stepper>(editor, "_width").Value = 2;
            Get<SegmentedControl>(editor, "_background").SelectedIndex = 1; // Light
            using var bitmap = Render(preview);

            Assert.Equal("3 apps · 2 rows", Get<Label>(editor, "_previewCaption").Text);
            var popup = preview.LastPopupBounds;
            Assert.Equal(PopupGeometry(3, 2), popup.Size);
            Assert.True(popup.Bottom <= preview.Height - 48 - 10, "popup sits 10px above the 48px taskbar");

        });
    }

    [Theory]
    [InlineData(10, 0.9)]
    [InlineData(50, 0.5)]
    [InlineData(100, 0.1)] // stored 100 renders at the 90% cap
    public void Preview_BlendsThePopupOverTheWallpaperAtTheRenderedOpacity(int transparency, double opacity)
    {
        RunOnStaThread(() =>
        {
            using var preview = new TaskbarPreview { Size = new Size(384, 264) };
            preview.ApplyTheme(ThemeCatalog.Default.Get("mint").Light, ResolvedMode.Light);
            using var wallpaper = Render(preview); // no popup yet

            preview.SetState(new PreviewState(1, 6, Group.LightColorHex, transparency, _ => null, _ => "A"));
            using var withPopup = Render(preview);

            var popup = preview.LastPopupBounds;
            Assert.Equal(PopupGeometry(1, 6), popup.Size);
            var x = popup.X + 2; // inside the popup, outside the plate
            var y = popup.Y + 2;
            var under = wallpaper.GetPixel(x, y);
            var actual = withPopup.GetPixel(x, y);
            var surface = ColorTranslator.FromHtml(Group.LightColorHex);
            Assert.InRange(actual.R, Blend(surface.R, under.R, opacity) - 2, Blend(surface.R, under.R, opacity) + 2);
            Assert.InRange(actual.G, Blend(surface.G, under.G, opacity) - 2, Blend(surface.G, under.G, opacity) + 2);
            Assert.InRange(actual.B, Blend(surface.B, under.B, opacity) - 2, Blend(surface.B, under.B, opacity) + 2);
        });
    }

    [Fact]
    public void Preview_TinyCard_DrawsNoPopupAndDoesNotThrow()
    {
        RunOnStaThread(() =>
        {
            using var preview = new TaskbarPreview { Size = new Size(40, 52) };
            preview.ApplyTheme(ThemeCatalog.Default.Get("mint").Dark, ResolvedMode.Dark);
            preview.SetState(new PreviewState(4, 4, Group.DarkColorHex, 10, _ => null, _ => "A"));

            using var bitmap = Render(preview);

            Assert.Equal(Rectangle.Empty, preview.LastPopupBounds);
        });
    }

    private static int Blend(int top, int under, double opacity) => (int)Math.Round((top * opacity) + (under * (1 - opacity)));

    // ---------------------------------------------------------------- change icon

    [Fact]
    public void ChangeIcon_FromAnImage_IsPendingUntilSaveThenWritesGroupIcon()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            Assert.True(GroupLnkWriter.Create("Alpha", Notepad(), _shortcuts).Success);
            using var editor = CreateEditor(store);
            var png = WritePng(Color.FromArgb(255, 30, 160, 90));
            var iconFile = Path.Combine(store.GetGroupDirectory("Alpha"), "GroupIcon.ico");

            Assert.Null(editor.SetPendingIconFromFile(png));
            Assert.True(editor.IsDirty);
            Assert.False(File.Exists(iconFile), "nothing is written before Save");

            Invoke(editor, "SaveGroup");

            Assert.True(File.Exists(iconFile));
            using (var icon = new Icon(iconFile, new Size(32, 32)))
            using (var bitmap = icon.ToBitmap())
            {
                Assert.Equal(Color.FromArgb(255, 30, 160, 90).ToArgb(), bitmap.GetPixel(16, 16).ToArgb());
            }

            var lnk = LnkResolver.Resolve(Path.Combine(_shortcuts, "Alpha.lnk"));
            Assert.Equal(iconFile, lnk!.IconLocation, ignoreCase: true);
            Assert.Contains("Taskbar shortcut updated", Get<Label>(editor, "_status").Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ChangeIcon_WhenTheIconCannotBeWritten_KeepsTheEditAndSaysSo()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            var iconFile = Path.Combine(store.GetGroupDirectory("Alpha"), "GroupIcon.ico");
            Directory.CreateDirectory(iconFile); // a folder where the icon must go: the move fails
            using var editor = CreateEditor(store);
            Assert.Null(editor.SetPendingIconFromFile(WritePng(Color.Orange)));

            Invoke(editor, "SaveGroup");

            Assert.True(editor.IsDirty, "the icon is still an unsaved edit");
            Assert.StartsWith("The group was saved, but its icon could not be written", Get<Label>(editor, "_status").Text, StringComparison.Ordinal);
            Assert.False(File.Exists(iconFile + ".tmp"));

            Directory.Delete(iconFile);
            Invoke(editor, "SaveGroup");
            Assert.False(editor.IsDirty);
            Assert.True(File.Exists(iconFile));
        });
    }

    [Fact]
    public void ChangeIcon_WithARename_LandsInTheRenamedGroup()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            var png = WritePng(Color.Purple);
            Assert.Null(editor.SetPendingIconFromFile(png));
            Get<TextBox>(editor, "_name").Text = "Beta";

            Invoke(editor, "SaveGroup");

            var iconFile = Path.Combine(store.GetGroupDirectory("Beta"), "GroupIcon.ico");
            Assert.Equal(EditorForm.BuildGroupIcon(png), File.ReadAllBytes(iconFile));
            Assert.Equal(new[] { "Beta" }, new GroupStore(_config).ListNames());
        });
    }

    [Fact]
    public void ChangeIcon_NoOps_DoNotMarkTheGroupDirty()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            var png = WritePng(Color.Teal);
            using var editor = CreateEditor(store);

            Invoke(editor, "UseDefaultIcon"); // the group has no custom icon
            Assert.False(editor.IsDirty);

            Assert.Null(editor.SetPendingIconFromFile(png));
            Invoke(editor, "SaveGroup");
            Assert.Null(editor.SetPendingIconFromFile(png)); // the same icon again
            Assert.False(editor.IsDirty);
        });
    }

    [Fact]
    public void ChangeIcon_UseDefault_RemovesTheCustomIconOnSave()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            var iconFile = Path.Combine(store.GetGroupDirectory("Alpha"), "GroupIcon.ico");
            using (var source = Solid(Color.Red))
            {
                File.WriteAllBytes(iconFile, IcoWriter.Write(source, new[] { 32 }));
            }

            using var editor = CreateEditor(store);
            Invoke(editor, "UseDefaultIcon");
            Assert.True(File.Exists(iconFile));

            Invoke(editor, "SaveGroup");

            Assert.False(File.Exists(iconFile));
        });
    }

    [Fact]
    public void ChangeIcon_IcoIsKeptAsIs_AndProgramsAreExtracted()
    {
        var source = Path.Combine(_root, "custom.ico");
        using (var image = Solid(Color.Blue))
        {
            File.WriteAllBytes(source, IcoWriter.Write(image, new[] { 16, 48 }));
        }

        Assert.Equal(File.ReadAllBytes(source), EditorForm.BuildGroupIcon(source));

        var fromExe = EditorForm.BuildGroupIcon(Notepad());
        using var stream = new MemoryStream(fromExe);
        using var icon = new Icon(stream, new Size(48, 48));
        using var bitmap = icon.ToBitmap();
        Assert.True(Enumerable.Range(0, 48).Any(x => bitmap.GetPixel(x, 24).A > 0), "notepad's icon has visible pixels");
    }

    [Fact]
    public void ChangeIcon_UnreadableFile_IsRejectedWithoutTouchingTheBuffer()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            var bad = Path.Combine(_root, "not-an-image.png");
            File.WriteAllText(bad, "this is text");

            var error = editor.SetPendingIconFromFile(bad);

            Assert.StartsWith("'not-an-image.png' cannot be used as an icon", error, StringComparison.Ordinal);
            Assert.False(editor.IsDirty);
        });
    }

    // ---------------------------------------------------------------- appearance page

    [Fact]
    public void AppearancePage_IsOnlyOfferedWithWritableSettings()
    {
        RunOnStaThread(() =>
        {
            using var readOnly = CreateEditor(Seed("Alpha"));
            Assert.False(Get<EditorSidebar>(readOnly, "_sidebar").ShowAppearance);

            using var writable = CreateEditor(new GroupStore(_config), new FakeAppearance());
            Assert.True(Get<EditorSidebar>(writable, "_sidebar").ShowAppearance);
        });
    }

    [Fact]
    public void AppearancePage_PaletteAndModeChangesApplyAndReportSaved()
    {
        RunOnStaThread(() =>
        {
            var appearance = new FakeAppearance();
            using var editor = CreateEditor(Seed("Alpha"), appearance);
            Invoke(editor, "ShowAppearancePage");
            var page = Get<AppearancePage>(editor, "_appearancePage");

            Click(page.PaletteTiles.Single(t => t.AccessibleName == "Coral"));
            Click(page.ModeButton(AppMode.Dark)); // PerformClick needs a shown window

            Assert.Equal(new AppearancePreferences(AppMode.Dark, "coral"), appearance.Preferences);
            Assert.Equal("Appearance saved", Get<Label>(editor, "_status").Text);
            Assert.Equal("Selected", page.PaletteTiles.Single(t => t.AccessibleName == "Coral").AccessibleDescription);
            Assert.True(page.ModeButton(AppMode.Dark).IsSelected);
            Assert.False(page.ModeButton(AppMode.System).IsSelected);
            Assert.Equal(ThemeCatalog.Default.Get("coral").Dark.Canvas, editor.BackColor);
        });
    }

    [Fact]
    public void AppearancePage_ArrowKeysMoveThroughThePalettes()
    {
        RunOnStaThread(() =>
        {
            var appearance = new FakeAppearance();
            using var editor = CreateEditor(Seed("Alpha"), appearance);
            Invoke(editor, "ShowAppearancePage");
            var page = Get<AppearancePage>(editor, "_appearancePage");
            var mint = page.PaletteTiles.Single(t => t.AccessibleName == "Mint");
            var onKeyDown = mint.GetType().GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;

            onKeyDown.Invoke(mint, new object[] { new KeyEventArgs(Keys.Right) });
            Assert.Equal("coral", appearance.Preferences.PaletteId);

            var coral = page.PaletteTiles.Single(t => t.AccessibleName == "Coral");
            onKeyDown.Invoke(coral, new object[] { new KeyEventArgs(Keys.Left) });
            Assert.Equal("mint", appearance.Preferences.PaletteId);
        });
    }

    [Fact]
    public void AppearancePage_FailedSaveKeepsTheSelectionAndShowsTheError()
    {
        RunOnStaThread(() =>
        {
            var appearance = new FakeAppearance { FailWith = "The appearance settings could not be saved: disk full" };
            using var editor = CreateEditor(Seed("Alpha"), appearance);
            Invoke(editor, "ShowAppearancePage");
            var page = Get<AppearancePage>(editor, "_appearancePage");

            Click(page.PaletteTiles.Single(t => t.AccessibleName == "Ocean"));

            Assert.Equal("mint", appearance.Preferences.PaletteId);
            Assert.Equal("The appearance settings could not be saved: disk full", Get<Label>(editor, "_status").Text);
            Assert.Equal("Selected", page.PaletteTiles.Single(t => t.AccessibleName == "Mint").AccessibleDescription);
        });
    }

    [Fact]
    public void AppearancePage_LeavesTheGroupEditBufferUntouched()
    {
        RunOnStaThread(() =>
        {
            using var editor = CreateEditor(Seed("Alpha", "Beta"), new FakeAppearance());
            Get<TextBox>(editor, "_name").Text = "Alpha Edited";
            var prompts = 0;
            editor.GuardPromptOverride = _ =>
            {
                prompts++;
                return EditorForm.GuardChoice.Cancel;
            };

            Invoke(editor, "ShowAppearancePage");
            Assert.True(editor.ShowingAppearance);
            Assert.False(editor.GroupActionsVisible);
            Assert.Null(editor.AcceptButton); // Enter on the page never runs the group Save
            Assert.Equal(-1, Get<EditorSidebar>(editor, "_sidebar").SelectedIndex);
            Assert.True(Get<EditorSidebar>(editor, "_sidebar").AppearanceSelected);

            // Back to the same group: no prompt, edits intact.
            ClickSidebar(editor, "Alpha");
            Assert.False(editor.ShowingAppearance);
            Assert.Equal(0, prompts);
            Assert.Equal("Alpha Edited", Get<TextBox>(editor, "_name").Text);
            Assert.True(editor.IsDirty);
            Assert.True(editor.GroupActionsVisible);
            Assert.Same(Get<ThemedButton>(editor, "_save"), editor.AcceptButton);
            Assert.Equal("Unsaved changes", Get<Label>(editor, "_status").Text);
        });
    }

    [Fact]
    public void ShowInExplorer_SelectsTheGroupShortcutOrOpensTheFolder()
    {
        RunOnStaThread(() =>
        {
            var launched = new List<string>();
            ShellHandoff.ExplorerLaunchOverride = launched.Add;
            try
            {
                var store = Seed("Alpha");
                using var editor = CreateEditor(store);
                var button = Get<ThemedButton>(editor, "_showInExplorer");
                Assert.Equal("Show in File Explorer", button.Text);
                Assert.Contains(button, Get<Panel>(editor, "_footer").Controls.Cast<Control>());

                // No .lnk yet: the shortcuts folder itself.
                Click(button);
                Assert.Equal("\"" + Path.GetFullPath(_shortcuts) + "\"", Assert.Single(launched));

                // With the group's .lnk: Explorer opens with it selected.
                File.WriteAllText(Path.Combine(_shortcuts, "Alpha.lnk"), "lnk");
                Click(button);
                Assert.Equal("/select,\"" + Path.GetFullPath(Path.Combine(_shortcuts, "Alpha.lnk")) + "\"", launched[1]);
            }
            finally
            {
                ShellHandoff.ExplorerLaunchOverride = null;
            }
        });
    }

    // ---------------------------------------------------------------- helpers

    private static Size PopupGeometry(int count, int width) => Dockaroo.Core.Popup.PopupGeometry.SizeFor(count, width);

    private EditorForm CreateEditor(GroupStore store, IEditorThemeSource? theme = null)
    {
        var pins = new PinnedStateCache(_shortcuts, Path.Combine(_root, "Pinned"), Notepad(), _ => null);
        return new EditorForm(store, listGroups: true, pinState: pins, shortcutsDirectory: _shortcuts, theme: theme ?? new StaticThemeSource("mint", ResolvedMode.Light));
    }

    private GroupStore Seed(string first, string? second = null, int entries = 2)
    {
        var store = new GroupStore(_config);
        foreach (var name in new[] { first, second }.Where(n => n is not null))
        {
            var group = new Group { Name = name!, Width = 6 };
            for (var i = 1; i <= entries; i++)
            {
                group.Shortcuts.Add(new ShortcutEntry { TargetPath = Notepad(), Name = $"App {i}" });
            }

            Assert.True(store.Save(group).Success);
        }

        return store;
    }

    private string WritePng(Color color)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png");
        using var bitmap = Solid(color);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static Bitmap Solid(Color color)
    {
        var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(color);
        return bitmap;
    }

    private static Bitmap Render(Control control)
    {
        var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        return bitmap;
    }

    private static void Click(Control control)
    {
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, new object[] { EventArgs.Empty });
    }

    private static void ClickSidebar(EditorForm editor, string name)
    {
        var sidebar = Get<EditorSidebar>(editor, "_sidebar");
        var index = sidebar.Items.Select(i => i.Name).ToList().IndexOf(name);
        typeof(EditorForm).GetMethod("OnSidebarGroupClicked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, new object[] { index });
    }

    private static T Get<T>(EditorForm editor, string field)
    {
        var info = typeof(EditorForm).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        return Assert.IsAssignableFrom<T>(info!.GetValue(editor));
    }

    private static void Invoke(EditorForm editor, string method)
    {
        var info = typeof(EditorForm).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(info);
        info!.Invoke(editor, null);
    }

    private static string Notepad() => Path.Combine(Environment.SystemDirectory, "notepad.exe");

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
                failure = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
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

    /// <summary>Writable appearance in memory; FailWith makes every save fail with that text.</summary>
    private sealed class FakeAppearance : IEditorThemeSource, IAppearanceSettings
    {
        private readonly Dictionary<(string, int), Icon> _icons = new();

        public event EventHandler? Changed;

        public string? FailWith { get; set; }

        public AppearancePreferences Preferences { get; private set; } = new(AppMode.Light, "mint");

        public ThemeTokens Tokens => ThemeCatalog.Default.Get(Preferences.PaletteId).For(EffectiveMode);

        public ResolvedMode EffectiveMode => Preferences.Resolve(systemUsesLight: true);

        public string PaletteId => Preferences.PaletteId;

        public Icon GetAppIcon(Size size) => GetIcon(Preferences.PaletteId, size);

        public Icon GetIcon(string paletteId, Size size)
        {
            if (!_icons.TryGetValue((paletteId, size.Width), out var icon))
            {
                using var stream = typeof(StaticThemeSource).Assembly.GetManifestResourceStream($"Dockaroo.App.Palettes.logo-{paletteId}.ico")!;
                icon = new Icon(stream, size);
                _icons[(paletteId, size.Width)] = icon;
            }

            return icon;
        }

        public string? SetMode(AppMode mode) => Apply(Preferences with { Mode = mode });

        public string? SetPalette(string paletteId) => Apply(Preferences with { PaletteId = paletteId });

        private string? Apply(AppearancePreferences next)
        {
            if (FailWith is not null)
            {
                return FailWith;
            }

            Preferences = next;
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
    }
}
