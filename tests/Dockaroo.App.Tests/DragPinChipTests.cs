using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.App.Editor;
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
    public void Chip_CarriesTheGroupTitleAndTheDragInstruction()
    {
        RunOnStaThread(() =>
        {
            var lnkPath = Path.Combine(_shortcuts, "My Group.lnk");

            using var chip = new DragPinChipForm("My Group", lnkPath, null);

            Assert.Equal("My Group", chip.TitleLabel.Text);
            Assert.Equal("Drag onto the taskbar to pin", DragPinChipForm.InstructionText);
            Assert.Equal("Drag onto the taskbar to pin", chip.InstructionLabel.Text);
            Assert.Equal(lnkPath, chip.ShortcutPath);
        });
    }

    [Fact]
    public void DragData_IsExactlyTheShortcutPathAsFileDrop()
    {
        var lnkPath = Path.Combine(_shortcuts, "My Group.lnk");

        var data = DragPinChipForm.CreateDragData(lnkPath);

        Assert.True(data.GetDataPresent(DataFormats.FileDrop));
        Assert.Equal(new[] { lnkPath }, Assert.IsType<string[]>(data.GetData(DataFormats.FileDrop)));
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
