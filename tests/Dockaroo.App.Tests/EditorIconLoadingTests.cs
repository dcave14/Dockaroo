using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Dockaroo.App.Editor;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Editor rows load icons off the UI thread through the shared cache (backlog P1-6). A fake
/// extractor sleeps per icon, so a synchronous editor would take seconds to open; the forms are
/// created but never shown, and the posted results are pumped with Application.DoEvents.
/// </summary>
public sealed class EditorIconLoadingTests : IDisposable
{
    private const int EntryCount = 20;
    private const int ExtractDelayMs = 200;

    private readonly string _root;
    private readonly System.Collections.Concurrent.ConcurrentBag<int> _extractorThreads = new();

    public EditorIconLoadingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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
    public void OpeningATwentyEntryGroup_DoesNotWaitForIconExtraction()
    {
        RunOnStaThread(() =>
        {
            var store = new GroupStore(Path.Combine(_root, "config"));
            var saved = store.Save(TwentyEntryGroup());
            Assert.True(saved.Success, saved.Error);
            using var icons = new IconCache(Path.Combine(_root, "icon-cache"), SlowRedExtractor);
            var pins = new PinnedStateCache(Path.Combine(_root, "Shortcuts"), Path.Combine(_root, "Pinned"), Path.Combine(Environment.SystemDirectory, "notepad.exe"), _ => null);

            using var editor = new EditorForm(store, listGroups: true, pinState: pins, shortcutsDirectory: Path.Combine(_root, "Shortcuts"), icons: icons);

            // The constructor returned before any icon arrived: rows start empty.
            var list = EntryList(editor);
            Assert.Equal(EntryCount, list.Entries.Count);
            Assert.All(Enumerable.Range(0, EntryCount), i => Assert.Null(list.RowImage(i)));

            PumpUntil(() => Enumerable.Range(0, EntryCount).All(i => list.RowImage(i) is not null));

            // Every extraction ran off the UI thread, exactly once per entry (no double load).
            Assert.Equal(EntryCount, _extractorThreads.Count);
            Assert.DoesNotContain(Environment.CurrentManagedThreadId, _extractorThreads);

            for (var i = 0; i < EntryCount; i++)
            {
                var image = Assert.IsType<Bitmap>(list.RowImage(i));
                Assert.Equal(Color.FromArgb(255, 255, 0, 0).ToArgb(), image.GetPixel(12, 12).ToArgb());
            }
        });
    }

    [Fact]
    public void DisposingRows_LeavesTheSharedCacheBitmapsUsable()
    {
        RunOnStaThread(() =>
        {
            using var icons = new IconCache(Path.Combine(_root, "icon-cache"), SlowRedExtractor);
            var group = TwentyEntryGroup();
            var list = new EntryListControl(icons);
            list.SetEntries(group.Shortcuts.Take(2));
            PumpUntil(() => list.RowImage(0) is not null && list.RowImage(1) is not null);
            var shown = (Bitmap)list.RowImage(0)!;

            list.Dispose();

            // The bitmap belongs to the cache (the popup draws it too): still alive.
            Assert.Equal(Color.FromArgb(255, 255, 0, 0).ToArgb(), shown.GetPixel(0, 0).ToArgb());
            Assert.True(icons.TryGet(IconSource.FromPath(group.Shortcuts[0].TargetPath), 24, out var cached));
            Assert.Same(shown, cached!.Bitmap);
        });
    }

    private IconExtractionResult SlowRedExtractor(IconSource source, int size)
    {
        _extractorThreads.Add(Environment.CurrentManagedThreadId);
        Thread.Sleep(ExtractDelayMs);
        var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Red);
        }

        return IconExtractionResult.Ok(bitmap);
    }

    private static Group TwentyEntryGroup()
    {
        // Paths need not exist: the fake extractor never touches them, and IconSource is pure.
        var group = new Group { Name = "Twenty", Width = 6 };
        for (var i = 1; i <= EntryCount; i++)
        {
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = $@"C:\Fake\App{i}\app{i}.exe", Name = $"App {i}" });
        }

        return group;
    }

    private static EntryListControl EntryList(EditorForm editor)
    {
        var field = typeof(EditorForm).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<EntryListControl>(field!.GetValue(editor));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.ElapsedMilliseconds < 15000, "icons were not delivered to the rows");
            Application.DoEvents();
            Thread.Sleep(10);
        }
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
}
