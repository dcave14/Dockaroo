using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.App.Editor;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Rename keeps the taskbar shortcut working (backlog P1-2). The editor is created but never
/// shown; the name box and Save are driven through reflection, and every file lives in a temp
/// root (config, Shortcuts, pinned folder).
/// </summary>
public sealed class EditorRenameTests : IDisposable
{
    private readonly string _root;
    private readonly string _config;
    private readonly string _shortcuts;
    private readonly string _pinned;

    public EditorRenameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        _config = Path.Combine(_root, "config");
        _shortcuts = Path.Combine(_root, "Shortcuts");
        _pinned = Path.Combine(_root, "UserPinned");
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

    [Fact]
    public void Rename_ReplacesTheOldShortcutWithOneForTheNewName()
    {
        RunOnStaThread(() =>
        {
            var store = SeedGroup("Alpha");
            var link = GroupLnkWriter.Create("Alpha", NotepadPath(), _shortcuts);
            Assert.True(link.Success, link.Error);
            var oldLnk = Path.Combine(_shortcuts, "Alpha.lnk");
            var newLnk = Path.Combine(_shortcuts, "Beta.lnk");

            using var editor = CreateEditor(store);
            Rename(editor, "Beta");

            Assert.False(File.Exists(oldLnk), "the old shortcut must be removed");
            Assert.True(File.Exists(newLnk), "a shortcut for the new name must exist");
            var info = LnkResolver.Resolve(newLnk);
            Assert.NotNull(info);
            Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Beta"), info!.AppUserModelId);
            Assert.Contains(EditorForm.RepinMessage, StatusText(editor), StringComparison.Ordinal);
            Assert.Equal(new[] { "Beta" }, store.ListNames());
        });
    }

    [Fact]
    public void Rename_CarriesTheGroupIconAndImageAndTheNewShortcutUsesTheIcon()
    {
        RunOnStaThread(() =>
        {
            var store = SeedGroup("Alpha");
            var iconBytes = File.ReadAllBytes(WriteTestIcon(Path.Combine(store.GetGroupDirectory("Alpha"), "GroupIcon.ico")));
            var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            File.WriteAllBytes(Path.Combine(store.GetGroupDirectory("Alpha"), "GroupImage.png"), imageBytes);
            Assert.True(GroupLnkWriter.Create("Alpha", NotepadPath(), _shortcuts).Success);

            using var editor = CreateEditor(store);
            Rename(editor, "Beta");

            var newIcon = Path.Combine(store.GetGroupDirectory("Beta"), "GroupIcon.ico");
            Assert.Equal(iconBytes, File.ReadAllBytes(newIcon));
            Assert.Equal(imageBytes, File.ReadAllBytes(Path.Combine(store.GetGroupDirectory("Beta"), "GroupImage.png")));
            Assert.False(Directory.Exists(store.GetGroupDirectory("Alpha")));
            var info = LnkResolver.Resolve(Path.Combine(_shortcuts, "Beta.lnk"));
            Assert.NotNull(info);
            Assert.Equal(newIcon, info!.IconLocation, ignoreCase: true);
        });
    }

    [Fact]
    public void Rename_WithoutAShortcut_CreatesNoneAndDoesNotAskToRepin()
    {
        RunOnStaThread(() =>
        {
            var store = SeedGroup("Alpha");

            using var editor = CreateEditor(store);
            Rename(editor, "Beta");

            Assert.Empty(Directory.GetFiles(_shortcuts));
            Assert.DoesNotContain(EditorForm.RepinMessage, StatusText(editor), StringComparison.Ordinal);
            Assert.StartsWith(EditorForm.SavedStatus, StatusText(editor), StringComparison.Ordinal);
        });
    }

    private EditorForm CreateEditor(GroupStore store)
    {
        // No pin is ever known (probe says "not pinned"), so the pinned-rename dialog never opens.
        var pins = new PinnedStateCache(_shortcuts, _pinned, NotepadPath(), _ => null);
        return new EditorForm(store, listGroups: true, pinState: pins, shortcutsDirectory: _shortcuts);
    }

    private GroupStore SeedGroup(string name)
    {
        var store = new GroupStore(_config);
        var group = new Group { Name = name };
        group.Shortcuts.Add(new ShortcutEntry { TargetPath = NotepadPath(), Name = "notepad" });
        var saved = store.Save(group);
        Assert.True(saved.Success, saved.Error);
        return store;
    }

    private static string WriteTestIcon(string path)
    {
        using var bitmap = new System.Drawing.Bitmap(16, 16);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Teal);
        }

        using var icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        using (var stream = File.Create(path))
        {
            icon.Save(stream);
        }

        return path;
    }

    private static void Rename(EditorForm editor, string newName)
    {
        var nameBox = (TextBox)Field(editor, "_name");
        nameBox.Text = newName;
        var save = typeof(EditorForm).GetMethod("SaveGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(save);
        save!.Invoke(editor, null);
    }

    private static string StatusText(EditorForm editor) => ((Label)Field(editor, "_status")).Text;

    private static object Field(EditorForm editor, string name)
    {
        var field = typeof(EditorForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(editor)!;
    }

    private static string NotepadPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(path), "notepad.exe was expected in the system directory.");
        return path;
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
}
