using System.Xml.Linq;
using Dockaroo.Core;
using Xunit;

namespace Dockaroo.Core.Tests;

/// <summary>
/// Proves the GroupStore copy-on-read / copy-on-write contract and the per-name Save/Delete
/// serialization added with the group editor (task-007). The popup show path relies on the
/// cache never aliasing caller state, so these tests mutate every instance the store touches.
/// </summary>
public sealed class GroupStoreConcurrencyTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Load_ReturnsIndependentCopies()
    {
        var store = new GroupStore(_temp.Path);
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        // First load comes from disk, second from the cache; both must be private copies.
        var first = store.Load("Alpha");
        var second = store.Load("Alpha");

        Assert.Equal(GroupLoadStatus.Loaded, first.Status);
        Assert.Equal(GroupLoadStatus.Loaded, second.Status);
        Assert.NotSame(first.Group, second.Group);
        Assert.NotSame(first.Group!.Shortcuts, second.Group!.Shortcuts);
        Assert.NotSame(first.Group.Shortcuts[0], second.Group.Shortcuts[0]);

        // Mutating one copy leaves the other and the cached state untouched.
        first.Group.Name = "Tampered";
        first.Group.Shortcuts[0].Name = "Tampered entry";
        first.Group.Shortcuts.Add(ShortcutEntry.Create(@"C:\Extra.exe"));

        Assert.Equal("Alpha", second.Group.Name);
        Assert.Equal("Game One", second.Group.Shortcuts[0].Name);
        Assert.Single(second.Group.Shortcuts);

        var third = store.Load("Alpha");
        Assert.Equal(GroupLoadStatus.Loaded, third.Status);
        Assert.Equal("Alpha", third.Group!.Name);
        Assert.Equal("Game One", third.Group.Shortcuts[0].Name);
        Assert.Single(third.Group.Shortcuts);
    }

    [Fact]
    public void TryGetCached_ReturnsIndependentCopies()
    {
        var store = new GroupStore(_temp.Path);
        Assert.True(store.Save(MakeGroup("Alpha")).Success);
        Assert.Equal(GroupLoadStatus.Loaded, store.Load("Alpha").Status);

        Assert.True(store.TryGetCached("Alpha", out var first));
        Assert.True(store.TryGetCached("Alpha", out var second));

        Assert.NotSame(first, second);
        first!.Opacity = 90;
        first.Shortcuts.Clear();

        Assert.Equal(40, second!.Opacity);
        Assert.Single(second.Shortcuts);

        // The cache itself must still hold the saved state.
        Assert.True(store.TryGetCached("Alpha", out var third));
        Assert.Equal(40, third!.Opacity);
        Assert.Single(third.Shortcuts);
    }

    [Fact]
    public void Save_CacheAndFileDoNotAliasCallerState()
    {
        var store = new GroupStore(_temp.Path);
        var group = MakeGroup("Alpha");
        Assert.True(store.Save(group).Success);

        // The store must have snapshotted the group: later caller edits stay invisible.
        group.Width = 19;
        group.Shortcuts[0].Name = "Changed after save";
        group.Shortcuts.Add(ShortcutEntry.Create(@"C:\late.exe"));

        Assert.True(store.TryGetCached("Alpha", out var cached));
        Assert.Equal(6, cached!.Width);
        Assert.Equal("Game One", cached.Shortcuts[0].Name);
        Assert.Single(cached.Shortcuts);

        var fromDisk = new GroupStore(_temp.Path).Load("Alpha");
        Assert.Equal(GroupLoadStatus.Loaded, fromDisk.Status);
        Assert.Equal(6, fromDisk.Group!.Width);
        Assert.Equal("Game One", fromDisk.Group.Shortcuts[0].Name);
        Assert.Single(fromDisk.Group.Shortcuts);
    }

    [Fact]
    public async Task Save_ConcurrentSameName_SerializesToExactlyOneSuccess()
    {
        var store = new GroupStore(_temp.Path);
        const int writers = 8;
        var start = new ManualResetEventSlim(false);
        var tasks = new Task<GroupSaveResult>[writers];
        for (var i = 0; i < writers; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() =>
            {
                start.Wait();
                var group = MakeGroup("Alpha");
                group.Width = 1 + index;
                return store.Save(group);
            });
        }

        start.Set();
        var results = await Task.WhenAll(tasks);

        // The uniqueness check runs under the per-name lock: exactly one write may win and
        // every loser gets the regular "already exists" failure instead of a race.
        Assert.Equal(1, results.Count(result => result.Success));
        foreach (var failure in results.Where(result => !result.Success))
        {
            Assert.NotNull(failure.Error);
            Assert.Contains("already exists", failure.Error, StringComparison.Ordinal);
        }

        // The persisted config is complete, valid and matches the winning write.
        var winnerWidth = 1 + Array.FindIndex(results, result => result.Success);
        var xmlPath = Path.Combine(store.GetGroupDirectory("Alpha"), GroupStore.ConfigFileName);
        var document = XDocument.Load(xmlPath);
        Assert.Equal("Group", document.Root!.Name.LocalName);
        Assert.Equal("Alpha", document.Root.Element("Name")?.Value);
        Assert.Single(document.Root.Element("Shortcuts")!.Elements("Shortcut").ToList());

        var loaded = new GroupStore(_temp.Path).Load("Alpha");
        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(winnerWidth, loaded.Group!.Width);
        Assert.Empty(Directory.GetFiles(store.GetGroupDirectory("Alpha"), "*.tmp"));
    }

    [Fact]
    public async Task Save_ConcurrentDifferentNames_AllSucceed()
    {
        var store = new GroupStore(_temp.Path);
        const int writers = 8;
        var start = new ManualResetEventSlim(false);
        var tasks = new Task<GroupSaveResult>[writers];
        for (var i = 0; i < writers; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() =>
            {
                start.Wait();
                return store.Save(MakeGroup("Group " + index));
            });
        }

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => Assert.True(result.Success, result.Error));

        var fresh = new GroupStore(_temp.Path);
        for (var i = 0; i < writers; i++)
        {
            Assert.Equal(GroupLoadStatus.Loaded, fresh.Load("Group " + i).Status);
        }

        Assert.Equal(writers, fresh.ListNames().Count);
    }

    [Fact]
    public async Task SaveAndDelete_SameName_SerializeWithoutPartialState()
    {
        var store = new GroupStore(_temp.Path);
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        var start = new ManualResetEventSlim(false);
        var save = Task.Run(() =>
        {
            start.Wait();
            return store.Save(MakeGroup("Alpha"), overwrite: true);
        });
        var delete = Task.Run(() =>
        {
            start.Wait();
            return store.Delete("Alpha");
        });

        start.Set();
        var saveResult = await save;
        var deleted = await delete;

        Assert.True(deleted);
        Assert.True(saveResult.Success, saveResult.Error);

        // Whichever ran last wins, but the intermediate state is never partial: either the
        // config loads completely or it is gone together with its directory.
        var xmlPath = Path.Combine(store.GetGroupDirectory("Alpha"), GroupStore.ConfigFileName);
        if (File.Exists(xmlPath))
        {
            Assert.Equal(GroupLoadStatus.Loaded, new GroupStore(_temp.Path).Load("Alpha").Status);
        }
        else
        {
            Assert.Equal(GroupLoadStatus.NotFound, new GroupStore(_temp.Path).Load("Alpha").Status);
        }

        Assert.False(Directory.Exists(store.GetGroupDirectory("Alpha")) && !File.Exists(xmlPath));
    }

    [Fact]
    public void Clone_ProducesDeepIndependentCopy()
    {
        var group = MakeGroup("Alpha");
        var clone = group.Clone();

        Assert.NotSame(group, clone);
        Assert.NotSame(group.Shortcuts, clone.Shortcuts);
        Assert.NotSame(group.Shortcuts[0], clone.Shortcuts[0]);

        clone.Name = "Other";
        clone.Shortcuts[0].Name = "Other entry";
        clone.Shortcuts.Clear();

        Assert.Equal("Alpha", group.Name);
        Assert.Equal("Game One", group.Shortcuts[0].Name);
        Assert.Single(group.Shortcuts);
    }

    private static Group MakeGroup(string name)
    {
        return new Group
        {
            Name = name,
            Width = 6,
            AllowOpenAll = false,
            ColorHex = "#A1B2C3",
            Opacity = 40,
            Shortcuts =
            {
                new ShortcutEntry
                {
                    TargetPath = @"C:\Games\Game1.exe",
                    Name = "Game One",
                    Arguments = "--fullscreen",
                    WorkingDirectory = @"C:\Games",
                },
            },
        };
    }
}
