using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.App.Editor;
using Dockaroo.App.Editor.Controls;
using Dockaroo.Core;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Content tests for the drag-to-pin chip: the visible title/instruction strings, the CF_HDROP
/// payload (exactly the created .lnk) and the editor's pinned/unpinned handoff decision driven
/// by a fake pin cache. No window is ever shown and no drag is ever performed.
/// </summary>
public sealed class DragPinChipTests : IDisposable
{
    private readonly string _root;
    private readonly string _shortcuts;
    private readonly string _pinned;
    private readonly string _config;
    private readonly string _exePath;

    public DragPinChipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        _shortcuts = Path.Combine(_root, "Shortcuts");
        _pinned = Path.Combine(_root, "UserPinned");
        _config = Path.Combine(_root, "config");
        Directory.CreateDirectory(_shortcuts);
        Directory.CreateDirectory(_pinned);
        Directory.CreateDirectory(_config);
        _exePath = NotepadPath();
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

    [Fact]
    public void Card_CarriesTheGroupTitleTheInstructionAndTheShortcut()
    {
        RunOnStaThread(() =>
        {
            var lnkPath = Path.Combine(_shortcuts, "My Group.lnk");
            using var card = new PinChipCard();
            Assert.False(card.IsShowing);

            card.ShowFor("My Group", lnkPath, null);

            Assert.True(card.IsShowing);
            Assert.Equal("My Group", card.Title);
            Assert.Equal("Drag onto the taskbar to pin", PinChipCard.InstructionText);
            Assert.Equal(lnkPath, card.ShortcutPath);

            card.Dismiss();
            Assert.False(card.IsShowing);
            Assert.Null(card.ShortcutPath);
        });
    }

    [Fact]
    public void Card_OffersShowInFileExplorerWithTheShortcutSelected()
    {
        RunOnStaThread(() =>
        {
            var lnkPath = Path.Combine(_shortcuts, "My Group.lnk");
            var launched = new List<string>();
            ShellHandoff.ExplorerLaunchOverride = launched.Add;
            try
            {
                using var card = new PinChipCard();
                card.ShowFor("My Group", lnkPath, null);

                Assert.Equal("Show in File Explorer", card.ShowInFolderLink.Text);
                Assert.Contains(card.ShowInFolderLink, card.Controls.Cast<Control>());
                typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(card.ShowInFolderLink, new object[] { new LinkLabelLinkClickedEventArgs(card.ShowInFolderLink.Links[0]) });

                Assert.Equal("/select,\"" + Path.GetFullPath(lnkPath) + "\"", Assert.Single(launched));
            }
            finally
            {
                ShellHandoff.ExplorerLaunchOverride = null;
            }
        });
    }

    [Fact]
    public void Editor_ShowsTheCardInsideTheWindowAfterCreatingAShortcut_AndHidesItOnGroupSwitch()
    {
        RunOnStaThread(() =>
        {
            var store = new GroupStore(_config);
            foreach (var name in new[] { "Alpha", "Beta" })
            {
                var group = new Group { Name = name };
                group.Shortcuts.Add(new ShortcutEntry { TargetPath = _exePath, Name = "Notepad" });
                Assert.True(store.Save(group).Success);
            }

            var pins = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);
            using var editor = new EditorForm(store, listGroups: true, pinState: pins, shortcutsDirectory: _shortcuts);
            editor.ClientSize = new Size(1440, 920);
            editor.PerformLayout();

            typeof(EditorForm).GetMethod("CreateTaskbarShortcut", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);

            var card = editor.PinChip;
            Assert.True(card.IsShowing);
            Assert.Equal("Alpha", card.Title);
            Assert.Equal(Path.Combine(_shortcuts, "Alpha.lnk"), card.ShortcutPath);
            Assert.Same(editor, card.Parent);
            Assert.True(editor.ClientRectangle.Contains(card.Bounds), $"card {card.Bounds} must sit inside the editor {editor.ClientRectangle}");

            // On "Create taskbar shortcut": same width and edges, bottom touching its top.
            var footer = typeof(EditorForm).GetField("_footer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor) as Control;
            var button = typeof(EditorForm).GetField("_createShortcut", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(editor) as Control;
            Assert.Equal(footer!.Left + button!.Left, card.Left);
            Assert.Equal(button.Width, card.Width);
            Assert.Equal(footer.Top + button.Top, card.Bottom);

            typeof(EditorForm).GetMethod("LoadGroupByName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, new object[] { "Beta" });
            Assert.False(card.IsShowing);
        });
    }

    [Fact]
    public void DragData_IsExactlyTheShortcutPathAsFileDrop()
    {
        var lnkPath = Path.Combine(_shortcuts, "My Group.lnk");

        var data = ShellHandoff.CreateDragData(lnkPath);

        Assert.True(data.GetDataPresent(DataFormats.FileDrop));
        Assert.Equal(new[] { lnkPath }, Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop)));
    }

    [Fact]
    public void ShellDragData_IsTheShellsOwnObjectWithIdListAndFileDrop()
    {
        RunOnStaThread(() =>
        {
            // A real .lnk: the shell builds its data object from an existing item only.
            var written = GroupLnkWriter.Create("Pin Me", _exePath, _shortcuts);
            Assert.True(written.Success, written.Error);
            var lnkPath = written.LnkPath!;

            var shellData = ShellHandoff.CreateShellDragData(lnkPath);

            // Not the WinForms fallback: the shell's COM object, as File Explorer drags it.
            Assert.IsNotType<DataObject>(shellData);
            // A raw COM object: reflection type checks cannot see its interfaces, a cast (QueryInterface) can.
            var view = new DataObject((System.Runtime.InteropServices.ComTypes.IDataObject)shellData);
            Assert.True(view.GetDataPresent("Shell IDList Array"), "the taskbar pin target needs the shell ID list");
            Assert.True(view.GetDataPresent(DataFormats.FileDrop));
            var files = Assert.IsType<string[]>(view.GetData(DataFormats.FileDrop));
            Assert.Equal(Path.GetFullPath(lnkPath), Path.GetFullPath(Assert.Single(files)), ignoreCase: true);
        });
    }

    [Fact]
    public void ShellDragData_FallsBackToTheFileDropForAMissingFile()
    {
        RunOnStaThread(() =>
        {
            var lnkPath = Path.Combine(_shortcuts, "Missing.lnk");

            var data = Assert.IsType<DataObject>(ShellHandoff.CreateShellDragData(lnkPath));

            Assert.Equal(new[] { lnkPath }, Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop)));
        });
    }

    [Fact]
    public void PinnedGroup_DecidesForTheAlreadyPinnedMessage()
    {
        RunOnStaThread(() =>
        {
            var link = GroupLnkWriter.Create("Alpha", _exePath, _shortcuts);
            Assert.True(link.Success, link.Error);
            var pins = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => true);
            using var editor = new EditorForm(new GroupStore(_config), pinState: pins);

            Assert.Equal(EditorForm.PinHandoff.AlreadyPinned, editor.DecidePinHandoff("Alpha"));
        });
    }

    [Fact]
    public void UnpinnedGroup_DecidesForTheDragChip()
    {
        RunOnStaThread(() =>
        {
            var pins = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);
            using var editor = new EditorForm(new GroupStore(_config), pinState: pins);

            Assert.Equal(EditorForm.PinHandoff.ShowDragChip, editor.DecidePinHandoff("Alpha"));
        });
    }

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
                failure = ex;
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

    private static string NotepadPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(path), "notepad.exe was expected in the system directory.");
        return path;
    }
}
