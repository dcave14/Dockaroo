using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.App.Editor;
using Dockaroo.App.Editor.Controls;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// The redesigned editor (task-015): dirty state, the Save / Discard / Cancel guard, popup
/// settings, list editing, the selected-shortcut card, live theme changes and the responsive
/// layout. The form is created but never shown; private members are driven by reflection and
/// every file lives in a temp root.
/// </summary>
public sealed class EditorShellTests : IDisposable
{
    private readonly string _root;
    private readonly string _config;
    private readonly string _shortcuts;
    private readonly string _pinned;

    public EditorShellTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        _config = Path.Combine(_root, "config");
        _shortcuts = Path.Combine(_root, "Shortcuts");
        _pinned = Path.Combine(_root, "Pinned");
        Directory.CreateDirectory(_config);
        Directory.CreateDirectory(_shortcuts);
        Directory.CreateDirectory(_pinned);
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

    // ---------------------------------------------------------------- dirty state and save

    [Fact]
    public void LoadedGroup_IsClean_AndAnEditMakesItDirty()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", "Beta");
            using var editor = CreateEditor(store);

            Assert.False(editor.IsDirty);
            Assert.Equal("Alpha", Get<Label>(editor, "_title").Text);
            Assert.Equal("2 shortcuts", Get<Label>(editor, "_subtitle").Text);
            Assert.False(Get<ThemedButton>(editor, "_save").Enabled);

            Get<TextBox>(editor, "_name").Text = "Alpha Two";

            Assert.True(editor.IsDirty);
            Assert.Equal("Unsaved changes", Get<Label>(editor, "_status").Text);
            Assert.Equal("Alpha Two", Get<Label>(editor, "_title").Text);
            Assert.True(Get<ThemedButton>(editor, "_save").Enabled);
        });
    }

    [Fact]
    public void Save_WritesThePopupSettingsAndCleansTheBuffer()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);

            Get<Stepper>(editor, "_width").Value = 3;
            Get<SegmentedControl>(editor, "_background").SelectedIndex = 1;
            Get<TransparencySlider>(editor, "_transparency").Value = 30;
            Get<ToggleSwitch>(editor, "_allowOpenAll").Checked = true;
            Assert.Equal("30%", Get<Label>(editor, "_transparencyValue").Text);
            var changes = 0;
            editor.GroupsChanged += (_, _) => changes++;
            Invoke(editor, "SaveGroup");
            Assert.Equal(1, changes);

            Assert.False(editor.IsDirty);
            Assert.StartsWith(EditorForm.SavedStatus, Get<Label>(editor, "_status").Text, StringComparison.Ordinal);
            Assert.False(Get<ThemedButton>(editor, "_save").Enabled);
            var saved = new GroupStore(_config).Load("Alpha").Group!;
            Assert.Equal(3, saved.Width);
            Assert.Equal(Group.LightColorHex, saved.ColorHex);
            Assert.Equal(30, saved.Opacity);
            Assert.True(saved.AllowOpenAll);
        });
    }

    [Fact]
    public void Save_WithAnInvalidName_KeepsTheEditsAndShowsTheErrorInline()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            Get<TextBox>(editor, "_name").Text = "Bad/Name";

            Invoke(editor, "SaveGroup");

            Assert.True(editor.IsDirty);
            Assert.Equal("Bad/Name", Get<TextBox>(editor, "_name").Text);
            Assert.StartsWith("Name must not have special characters", Get<Label>(editor, "_nameError").Text, StringComparison.Ordinal);
            Assert.True(Get<ThemedField>(editor, "_nameField").HasError);
            Assert.Equal(new[] { "Alpha" }, new GroupStore(_config).ListNames());
        });
    }

    // ---------------------------------------------------------------- guard

    [Fact]
    public void SwitchingGroups_WhileDirty_CancelStaysAndKeepsTheEdits()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", "Beta");
            using var editor = CreateEditor(store);
            Get<TextBox>(editor, "_name").Text = "Alpha Edited";
            var prompts = new List<string>();
            editor.GuardPromptOverride = name =>
            {
                prompts.Add(name);
                return EditorForm.GuardChoice.Cancel;
            };

            ClickSidebarGroup(editor, "Beta");

            Assert.Equal(new[] { "'Alpha Edited'" }, prompts);
            Assert.Equal("Alpha Edited", Get<TextBox>(editor, "_name").Text);
            Assert.True(editor.IsDirty);
            Assert.Equal("Alpha", SelectedSidebarName(editor));
        });
    }

    [Fact]
    public void SwitchingGroups_WhileDirty_DiscardLoadsTheOtherGroup()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", "Beta");
            using var editor = CreateEditor(store);
            Get<TextBox>(editor, "_name").Text = "Alpha Edited";
            editor.GuardPromptOverride = _ => EditorForm.GuardChoice.Discard;

            ClickSidebarGroup(editor, "Beta");

            Assert.Equal("Beta", Get<TextBox>(editor, "_name").Text);
            Assert.False(editor.IsDirty);
            Assert.Equal("Beta", SelectedSidebarName(editor));
            Assert.Equal(new[] { "Alpha", "Beta" }, new GroupStore(_config).ListNames().OrderBy(n => n));
        });
    }

    [Fact]
    public void SwitchingGroups_WhileDirty_SaveSavesThenSwitches()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", "Beta");
            using var editor = CreateEditor(store);
            Get<Stepper>(editor, "_width").Value = 2;
            editor.GuardPromptOverride = _ => EditorForm.GuardChoice.Save;

            ClickSidebarGroup(editor, "Beta");

            Assert.Equal("Beta", Get<TextBox>(editor, "_name").Text);
            Assert.Equal(2, new GroupStore(_config).Load("Alpha").Group!.Width);
        });
    }

    [Fact]
    public void SwitchingGroups_SaveThatFails_StaysOnTheEditedGroup()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", "Beta");
            using var editor = CreateEditor(store);
            Get<TextBox>(editor, "_name").Text = string.Empty;
            editor.GuardPromptOverride = _ => EditorForm.GuardChoice.Save;

            ClickSidebarGroup(editor, "Beta");

            Assert.Equal(string.Empty, Get<TextBox>(editor, "_name").Text);
            Assert.True(editor.IsDirty);
            Assert.Equal("Must select a name", Get<Label>(editor, "_nameError").Text);
        });
    }

    [Fact]
    public void ClosingWhileDirty_CancelKeepsTheWindowOpen()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            Get<TextBox>(editor, "_name").Text = "Alpha Edited";
            editor.GuardPromptOverride = _ => EditorForm.GuardChoice.Cancel;

            var args = new FormClosingEventArgs(CloseReason.UserClosing, cancel: false);
            typeof(EditorForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, new object[] { args });

            Assert.True(args.Cancel);
        });
    }

    [Fact]
    public void NewGroup_StartsEmptyWithNoSidebarSelection()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);

            Invoke(editor, "StartNewGroup");

            Assert.Equal(string.Empty, Get<TextBox>(editor, "_name").Text);
            Assert.Equal("New group", Get<Label>(editor, "_title").Text);
            Assert.Equal("0 shortcuts", Get<Label>(editor, "_subtitle").Text);
            Assert.Equal(-1, Get<EditorSidebar>(editor, "_sidebar").SelectedIndex);
            Assert.False(Get<ThemedButton>(editor, "_delete").Enabled);
        });
    }

    // ---------------------------------------------------------------- shortcuts

    [Fact]
    public void ListEdits_ReorderRemoveRename_AreSavedAndMarkDirty()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", entries: 3);
            using var editor = CreateEditor(store);
            var list = Get<EntryListControl>(editor, "_entries");

            list.MoveEntry(2, 0);
            Assert.True(editor.IsDirty);
            list.RemoveAt(1);
            list.NameBoxAt(0).Text = "Renamed";
            Invoke(editor, "SaveGroup");

            var saved = new GroupStore(_config).Load("Alpha").Group!;
            Assert.Equal(new[] { "Renamed", "App 2" }, saved.Shortcuts.Select(s => s.Name));
            Assert.Equal("2 / 20", Get<Label>(editor, "_shortcutCount").Text);
            Assert.Equal("Drop apps, shortcuts, or folders here", list.DropPrompt);
        });
    }

    [Fact]
    public void SelectedShortcutCard_FollowsTheSelectionAndEditsTheEntry()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", entries: 2, arguments: "--one");
            using var editor = CreateEditor(store);
            var list = Get<EntryListControl>(editor, "_entries");
            var arguments = Get<TextBox>(editor, "_arguments");

            Assert.False(Get<ThemedField>(editor, "_argumentsField").Enabled);
            Assert.StartsWith("Select a shortcut", Get<Label>(editor, "_selectedSummary").Text, StringComparison.Ordinal);

            list.SelectIndex(1);
            Assert.True(Get<ThemedField>(editor, "_argumentsField").Enabled);
            Assert.Equal("--one", arguments.Text);
            Assert.Equal("App 2", Get<Label>(editor, "_selectedSummary").Text);
            Assert.False(editor.IsDirty); // showing a selection is not an edit

            arguments.Text = "--two";
            Assert.True(editor.IsDirty);
            Assert.Equal("--two", list.Entries[1].Arguments);
        });
    }

    [Fact]
    public void CreateTaskbarShortcut_SavesADirtyGroupFirst()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store, pinned: true); // already pinned: no chip window
            Get<Stepper>(editor, "_width").Value = 5;

            Invoke(editor, "CreateTaskbarShortcut");

            Assert.False(editor.IsDirty);
            Assert.Equal(5, new GroupStore(_config).Load("Alpha").Group!.Width);
            var info = LnkResolver.Resolve(Path.Combine(_shortcuts, "Alpha.lnk"));
            Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), info!.AppUserModelId);
            Assert.Equal("Already pinned to the taskbar.", Get<Label>(editor, "_status").Text);
        });
    }

    [Fact]
    public void CreateTaskbarShortcut_WhenTheSaveFails_WritesNothing()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store, pinned: true);
            Get<TextBox>(editor, "_name").Text = string.Empty;

            Invoke(editor, "CreateTaskbarShortcut");

            Assert.Empty(Directory.GetFiles(_shortcuts));
            Assert.True(editor.IsDirty);
        });
    }

    // ---------------------------------------------------------------- review round 1

    [Fact]
    public void Transparency_OffStepValueIsKeptUntilTheSliderMoves()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", opacity: 35);
            using var editor = CreateEditor(store);
            Assert.Equal("35%", Get<Label>(editor, "_transparencyValue").Text);
            Assert.False(editor.IsDirty);

            Get<Stepper>(editor, "_width").Value = 2; // an unrelated edit
            Invoke(editor, "SaveGroup");
            Assert.Equal(35, new GroupStore(_config).Load("Alpha").Group!.Opacity);

            Get<TransparencySlider>(editor, "_transparency").Value = 50;
            Assert.Equal("50%", Get<Label>(editor, "_transparencyValue").Text);
            Invoke(editor, "SaveGroup");
            Assert.Equal(50, new GroupStore(_config).Load("Alpha").Group!.Opacity);
        });
    }

    [Theory]
    [InlineData("Discard", false, true)]
    [InlineData("Save", true, true)]
    [InlineData("Cancel", false, false)]
    public void ExitAndClose_FollowTheGuardChoice(string choiceName, bool expectSaved, bool expectClosed)
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            Get<Stepper>(editor, "_width").Value = 3;
            var choice = Enum.Parse<EditorForm.GuardChoice>(choiceName);
            editor.GuardPromptOverride = _ => choice;

            var args = new FormClosingEventArgs(CloseReason.ApplicationExitCall, cancel: false);
            typeof(EditorForm).GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, new object[] { args });

            Assert.Equal(!expectClosed, args.Cancel);
            Assert.Equal(expectSaved ? 3 : 6, new GroupStore(_config).Load("Alpha").Group!.Width);
        });
    }

    [Fact]
    public void TryCloseForExit_CancelKeepsTheEditorAndItsEdits()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            Get<TextBox>(editor, "_name").Text = "Alpha Edited";
            editor.GuardPromptOverride = _ => EditorForm.GuardChoice.Cancel;

            Assert.False(editor.TryCloseForExit());
            Assert.False(editor.IsDisposed);
            Assert.Equal("Alpha Edited", Get<TextBox>(editor, "_name").Text);
        });
    }

    [Fact]
    public void Delete_OpensTheNextGroupAndReportsTheChange()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", "Beta");
            using var editor = CreateEditor(store);
            var changes = 0;
            editor.GroupsChanged += (_, _) => changes++;
            editor.DeleteConfirmOverride = name => name == "Alpha";

            Invoke(editor, "DeleteGroup");

            Assert.Equal(new[] { "Beta" }, new GroupStore(_config).ListNames());
            Assert.Equal("Beta", Get<TextBox>(editor, "_name").Text);
            Assert.Equal("Beta", SelectedSidebarName(editor));
            Assert.Equal("Deleted 'Alpha'.", Get<Label>(editor, "_status").Text);
            Assert.False(editor.IsDirty);
            Assert.Equal(1, changes);

            Invoke(editor, "DeleteGroup"); // Beta: the override says no
            Assert.Equal(new[] { "Beta" }, new GroupStore(_config).ListNames());
        });
    }

    [Fact]
    public void Delete_TheLastGroup_LeavesAFreshNewGroup()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            editor.DeleteConfirmOverride = _ => true;

            Invoke(editor, "DeleteGroup");

            Assert.Empty(new GroupStore(_config).ListNames());
            Assert.Equal("New group", Get<Label>(editor, "_title").Text);
            Assert.False(Get<ThemedButton>(editor, "_delete").Enabled);

            // A later save creates a new group instead of resurrecting the deleted one.
            Get<TextBox>(editor, "_name").Text = "Gamma";
            Get<EntryListControl>(editor, "_entries").AddTargets(new[] { NotepadPath() });
            Invoke(editor, "SaveGroup");
            Assert.Equal(new[] { "Gamma" }, new GroupStore(_config).ListNames());
        });
    }

    [Fact]
    public void RowKeyboard_DeleteRemovesAltArrowsMove()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", entries: 3);
            using var editor = CreateEditor(store);
            _ = editor.Handle;
            var list = Get<EntryListControl>(editor, "_entries");

            Assert.True(list.ProcessRowKey(0, Keys.Down | Keys.Alt, fromNameBox: true));
            Assert.Equal(new[] { "App 2", "App 1", "App 3" }, list.Entries.Select(e => e.Name));

            Assert.False(list.ProcessRowKey(2, Keys.Delete, fromNameBox: true)); // Delete edits text there
            Assert.True(list.ProcessRowKey(2, Keys.Delete, fromNameBox: false));
            Application.DoEvents();
            Assert.Equal(new[] { "App 2", "App 1" }, list.Entries.Select(e => e.Name));
            Assert.True(editor.IsDirty);
        });
    }

    [Fact]
    public void BackgroundArrowKeys_MoveWithoutOpeningTheColourPicker()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            var background = Get<SegmentedControl>(editor, "_background");
            var activations = 0;
            background.SegmentClicked += (_, _) => activations++;
            var onKeyDown = typeof(SegmentedControl).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;

            onKeyDown.Invoke(background, new object[] { new KeyEventArgs(Keys.Right) });
            onKeyDown.Invoke(background, new object[] { new KeyEventArgs(Keys.Right) });

            Assert.Equal(2, background.SelectedIndex);
            Assert.Equal(0, activations);
            Assert.True(editor.IsDirty);
        });
    }

    // ---------------------------------------------------------------- theme and layout

    [Fact]
    public void ThemeChange_RepaintsWithoutTouchingTheEditBuffer()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha", entries: 2);
            var theme = new SwitchableTheme("mint", ResolvedMode.Light);
            using var editor = CreateEditor(store, theme: theme);
            var list = Get<EntryListControl>(editor, "_entries");
            list.SelectIndex(1);
            Get<TextBox>(editor, "_name").Text = "Alpha Edited";

            theme.Switch("coral", ResolvedMode.Dark);

            var coral = ThemeCatalog.Default.Get("coral").Dark;
            Assert.Equal(coral.Canvas, editor.BackColor);
            Assert.Equal(coral.Surface, Get<CardPanel>(editor, "_shortcutsCard").BackColor);
            Assert.Equal(coral.Sidebar, Get<EditorSidebar>(editor, "_sidebar").BackColor);
            Assert.Equal("Alpha Edited", Get<TextBox>(editor, "_name").Text);
            Assert.True(editor.IsDirty);
            Assert.Equal(1, list.SelectedIndex);
        });
    }

    [Fact]
    public void Layout_WideWindowHasTwoColumns_NarrowWindowStacks()
    {
        RunOnStaThread(() =>
        {
            var store = Seed("Alpha");
            using var editor = CreateEditor(store);
            _ = editor.Handle;
            var shortcutsCard = Get<CardPanel>(editor, "_shortcutsCard");
            var selectedCard = Get<CardPanel>(editor, "_selectedCard");
            var popupCard = Get<CardPanel>(editor, "_popupCard");
            var sidebar = Get<EditorSidebar>(editor, "_sidebar");

            editor.Size = new Size(1440, 960);
            editor.PerformLayout();
            Assert.Equal(240, sidebar.Width);
            Assert.Equal(432, popupCard.Width);
            Assert.True(popupCard.Left > shortcutsCard.Right, "popup card sits in the right column");
            Assert.Equal(new[] { 120, 272 }, new[] { Get<CardPanel>(editor, "_identityCard").Top, shortcutsCard.Top });

            editor.Size = new Size(1000, 760);
            editor.PerformLayout();
            Assert.Equal(208, sidebar.Width);
            Assert.Equal(shortcutsCard.Left, popupCard.Left);
            Assert.Equal(shortcutsCard.Width, popupCard.Width);
            Assert.True(popupCard.Top > selectedCard.Bottom, "popup card stacks below the main column");
            Assert.True(Get<Panel>(editor, "_content").AutoScrollMinSize.Height > Get<Panel>(editor, "_content").ClientSize.Height);
        });
    }

    // ---------------------------------------------------------------- helpers

    private EditorForm CreateEditor(GroupStore store, bool pinned = false, IEditorThemeSource? theme = null)
    {
        var pins = new PinnedStateCache(_shortcuts, _pinned, NotepadPath(), _ => pinned ? true : null);
        return new EditorForm(store, listGroups: true, pinState: pins, shortcutsDirectory: _shortcuts, theme: theme ?? new StaticThemeSource("mint", ResolvedMode.Light));
    }

    private GroupStore Seed(string first, string? second = null, int entries = 2, string arguments = "", int opacity = 10)
    {
        var store = new GroupStore(_config);
        foreach (var name in new[] { first, second }.Where(n => n is not null))
        {
            var group = new Group { Name = name!, Width = 6, Opacity = opacity };
            for (var i = 1; i <= entries; i++)
            {
                group.Shortcuts.Add(new ShortcutEntry { TargetPath = NotepadPath(), Name = $"App {i}", Arguments = arguments });
            }

            var saved = store.Save(group);
            Assert.True(saved.Success, saved.Error);
        }

        return store;
    }

    private static void ClickSidebarGroup(EditorForm editor, string name)
    {
        var sidebar = Get<EditorSidebar>(editor, "_sidebar");
        var index = sidebar.Items.Select(i => i.Name).ToList().IndexOf(name);
        Assert.True(index >= 0, $"'{name}' is not in the sidebar");
        typeof(EditorForm).GetMethod("OnSidebarGroupClicked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, new object[] { index });
    }

    private static string? SelectedSidebarName(EditorForm editor)
    {
        var sidebar = Get<EditorSidebar>(editor, "_sidebar");
        return sidebar.SelectedIndex >= 0 ? sidebar.Items[sidebar.SelectedIndex].Name : null;
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

    private static string NotepadPath() => Path.Combine(Environment.SystemDirectory, "notepad.exe");

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

    /// <summary>A theme source the test can flip, raising Changed like ThemeService does.</summary>
    private sealed class SwitchableTheme : IEditorThemeSource
    {
        private StaticThemeSource _current;

        public SwitchableTheme(string palette, ResolvedMode mode)
        {
            _current = new StaticThemeSource(palette, mode);
        }

        public event EventHandler? Changed;

        public ThemeTokens Tokens => _current.Tokens;

        public ResolvedMode EffectiveMode => _current.EffectiveMode;

        public string PaletteId => _current.PaletteId;

        public Icon GetAppIcon(Size size) => _current.GetAppIcon(size);

        public void Switch(string palette, ResolvedMode mode)
        {
            _current = new StaticThemeSource(palette, mode);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
