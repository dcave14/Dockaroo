using System.Xml.Linq;
using Dockaroo.Core;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class GroupStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryField()
    {
        var store = CreateStore();
        var group = MakeGroup("My Games");
        group.Shortcuts.Add(new ShortcutEntry
        {
            TargetPath = @"C:\Tools\Editor.lnk",
            Name = "Editor",
            Arguments = string.Empty,
            WorkingDirectory = @"C:\Tools",
        });

        Assert.True(store.Save(group).Success);

        var result = new GroupStore(_temp.Path).Load("My Games");

        Assert.Equal(GroupLoadStatus.Loaded, result.Status);
        Assert.Null(result.Error);
        var loaded = Assert.IsType<Group>(result.Group);
        Assert.Equal(group.Name, loaded.Name);
        Assert.Equal(group.Width, loaded.Width);
        Assert.Equal(group.AllowOpenAll, loaded.AllowOpenAll);
        Assert.Equal(group.ColorHex, loaded.ColorHex);
        Assert.Equal(group.Opacity, loaded.Opacity);
        Assert.Equal(2, loaded.Shortcuts.Count);
        Assert.Equal(@"C:\Games\Game1.exe", loaded.Shortcuts[0].TargetPath);
        Assert.Equal("Game One", loaded.Shortcuts[0].Name);
        Assert.Equal("--fullscreen", loaded.Shortcuts[0].Arguments);
        Assert.Equal(@"C:\Games", loaded.Shortcuts[0].WorkingDirectory);
        Assert.Equal(@"C:\Tools\Editor.lnk", loaded.Shortcuts[1].TargetPath);
        Assert.Equal("Editor", loaded.Shortcuts[1].Name);
        Assert.Equal(string.Empty, loaded.Shortcuts[1].Arguments);
        Assert.Equal(@"C:\Tools", loaded.Shortcuts[1].WorkingDirectory);
    }

    [Fact]
    public void Save_WritesGroupXmlUnderMappedFolderName()
    {
        var store = CreateStore();

        Assert.True(store.Save(MakeGroup("My Games")).Success);

        var xmlPath = Path.Combine(_temp.Path, "My_Games", GroupStore.ConfigFileName);
        Assert.True(File.Exists(xmlPath));
        var document = XDocument.Load(xmlPath);
        Assert.Equal("Group", document.Root!.Name.LocalName);
        Assert.Equal("My Games", document.Root.Element("Name")?.Value);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(xmlPath)!, "*.tmp"));
    }

    [Fact]
    public void Save_RejectsInvalidGroup()
    {
        var store = CreateStore();
        var group = MakeGroup("Bad");
        group.Width = 0;

        var result = store.Save(group);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(GroupLoadStatus.NotFound, store.Load("Bad").Status);
    }

    [Fact]
    public void Save_RejectsNullGroup()
    {
        var store = CreateStore();

        var result = store.Save(null!);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Save_RejectsDuplicateName()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        var result = store.Save(MakeGroup("Alpha"));

        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_RejectsDuplicateName_DifferentCase()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        var result = store.Save(MakeGroup("alpha"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Save_WithOverwrite_ReplacesExistingConfig()
    {
        var store = CreateStore();
        var group = MakeGroup("Alpha");
        Assert.True(store.Save(group).Success);

        group.Width = 3;
        Assert.True(store.Save(group, overwrite: true).Success);

        var loaded = new GroupStore(_temp.Path).Load("Alpha");
        Assert.Equal(GroupLoadStatus.Loaded, loaded.Status);
        Assert.Equal(3, loaded.Group!.Width);
        Assert.Empty(Directory.GetFiles(store.GetGroupDirectory("Alpha"), "*.tmp"));
    }

    [Fact]
    public void Save_Failure_LeavesNoPartialFileAndNoTempFile()
    {
        var store = CreateStore();
        var group = MakeGroup("Alpha");
        Assert.True(store.Save(group).Success);

        // Block the destination: group.xml becomes a directory, so the final move must fail
        // after the temp file was already written.
        var xmlPath = XmlPath(store, "Alpha");
        File.Delete(xmlPath);
        Directory.CreateDirectory(xmlPath);

        group.Width = 3;
        var result = store.Save(group, overwrite: true);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.True(Directory.Exists(xmlPath));
        Assert.Empty(Directory.GetFiles(store.GetGroupDirectory("Alpha")));

        // A failed save must not report the group as present: the cached entry is dropped
        // and the on-disk state has no config file.
        Assert.False(store.TryGetCached("Alpha", out _));
        Assert.Equal(GroupLoadStatus.NotFound, store.Load("Alpha").Status);
        Assert.Empty(store.ListNames());
    }

    [Fact]
    public void Save_PreExistingEmptyFolder_IsNotTreatedAsExistingGroup()
    {
        var store = CreateStore();
        Directory.CreateDirectory(store.GetGroupDirectory("Ghost"));

        var result = store.Save(MakeGroup("Ghost"), overwrite: false);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        var reloaded = new GroupStore(_temp.Path).Load("Ghost");
        Assert.Equal(GroupLoadStatus.Loaded, reloaded.Status);
        Assert.Equal("Ghost", reloaded.Group!.Name);
    }

    [Fact]
    public void Save_FailedWrite_LeavesNoDirectoryAndNoListEntry()
    {
        var store = CreateStore();

        // Block the destination with a foreign file so the save fails after the name check.
        var groupDirectory = store.GetGroupDirectory("Ghost");
        File.WriteAllText(groupDirectory, "foreign");

        var result = store.Save(MakeGroup("Ghost"), overwrite: false);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.True(File.Exists(groupDirectory));
        Assert.Equal("foreign", File.ReadAllText(groupDirectory));
        Assert.False(store.TryGetCached("Ghost", out _));
        Assert.Equal(GroupLoadStatus.NotFound, store.Load("Ghost").Status);
        Assert.Empty(store.ListNames());
    }

    [Fact]
    public void Save_WhenRootPathIsAFile_FailsWithoutCrash()
    {
        var rootFile = Path.Combine(_temp.Path, "not-a-directory");
        File.WriteAllText(rootFile, "x");
        var store = new GroupStore(rootFile);

        var result = store.Save(MakeGroup("Alpha"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(GroupLoadStatus.NotFound, store.Load("Alpha").Status);
    }

    [Fact]
    public void Load_MissingGroup_ReturnsNotFoundWithoutThrowing()
    {
        var store = CreateStore();

        var result = store.Load("Missing");

        Assert.Equal(GroupLoadStatus.NotFound, result.Status);
        Assert.Null(result.Group);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_InvalidName_ReturnsNotFoundWithoutThrowing()
    {
        var store = CreateStore();

        Assert.Equal(GroupLoadStatus.NotFound, store.Load(string.Empty).Status);
        Assert.Equal(GroupLoadStatus.NotFound, store.Load("../escape").Status);
    }

    [Fact]
    public void Load_CorruptXml_ReturnsCorruptWithoutThrowing()
    {
        var store = CreateStore();
        var xmlPath = XmlPath(store, "Broken");
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
        File.WriteAllText(xmlPath, "this is not xml");

        var result = store.Load("Broken");

        Assert.Equal(GroupLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Group);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_SemanticallyInvalidXml_ReturnsCorrupt()
    {
        var store = CreateStore();
        WriteConfig(store, "Broken", "<Group><Name>Bad@Name</Name><Width>6</Width><Opacity>10</Opacity></Group>");

        var result = store.Load("Broken");

        Assert.Equal(GroupLoadStatus.Corrupt, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_EmbeddedNameMismatch_ReturnsCorruptWithoutThrowing()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        // Tamper the saved config so it claims to belong to a different group.
        var xmlPath = XmlPath(store, "Alpha");
        var tampered = File.ReadAllText(xmlPath).Replace("<Name>Alpha</Name>", "<Name>Beta</Name>", StringComparison.Ordinal);
        File.WriteAllText(xmlPath, tampered);
        Assert.Contains("<Name>Beta</Name>", tampered, StringComparison.Ordinal);

        var result = new GroupStore(_temp.Path).Load("Alpha");

        Assert.Equal(GroupLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Group);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_RenamedFolderWithOldConfig_ReturnsCorruptWithoutThrowing()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        // Rename the folder; the config inside still claims to be "Alpha".
        Directory.Move(store.GetGroupDirectory("Alpha"), store.GetGroupDirectory("Beta"));

        var reader = new GroupStore(_temp.Path);
        Assert.Equal(GroupLoadStatus.NotFound, reader.Load("Alpha").Status);

        var result = reader.Load("Beta");

        Assert.Equal(GroupLoadStatus.Corrupt, result.Status);
        Assert.Null(result.Group);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Load_MissingColorHex_FallsBackToDark()
    {
        var store = CreateStore();
        WriteConfig(store, "Legacy", "<Group><Name>Legacy</Name><Width>6</Width><AllowOpenAll>true</AllowOpenAll><Opacity>20</Opacity></Group>");

        var result = store.Load("Legacy");

        Assert.Equal(GroupLoadStatus.Loaded, result.Status);
        Assert.Equal(Group.DarkColorHex, result.Group!.ColorHex);
        Assert.True(result.Group.AllowOpenAll);
        Assert.Empty(result.Group.Shortcuts);
    }

    [Fact]
    public void Load_SecondCall_IsServedFromCacheWithoutDisk()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);
        var first = store.Load("Alpha");
        Assert.Equal(GroupLoadStatus.Loaded, first.Status);

        // Removing the file proves the next load never touches disk.
        File.Delete(XmlPath(store, "Alpha"));

        var second = store.Load("Alpha");

        Assert.Equal(GroupLoadStatus.Loaded, second.Status);

        // Copy semantics: both calls return independent instances with identical content.
        Assert.NotSame(first.Group, second.Group);
        Assert.Equal(first.Group!.Name, second.Group!.Name);
        Assert.Equal(first.Group.Width, second.Group.Width);
        Assert.Equal(first.Group.Shortcuts.Count, second.Group.Shortcuts.Count);
        Assert.NotSame(first.Group.Shortcuts, second.Group.Shortcuts);
        Assert.NotSame(first.Group.Shortcuts[0], second.Group.Shortcuts[0]);
    }

    [Fact]
    public void TryGetCached_OnlyReturnsLoadedGroups()
    {
        var writer = CreateStore();
        Assert.True(writer.Save(MakeGroup("Alpha")).Success);
        Assert.True(writer.TryGetCached("Alpha", out _));

        var store = new GroupStore(_temp.Path);
        Assert.False(store.TryGetCached("Alpha", out var group));
        Assert.Null(group);

        Assert.Equal(GroupLoadStatus.Loaded, store.Load("Alpha").Status);
        Assert.True(store.TryGetCached("Alpha", out group));
        Assert.NotNull(group);
    }

    [Fact]
    public void Preload_LoadsAllGroupsIntoCache()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);
        Assert.True(store.Save(MakeGroup("Beta")).Success);

        var fresh = new GroupStore(_temp.Path);
        Assert.Equal(2, fresh.Preload());
        Assert.True(fresh.TryGetCached("Alpha", out _));
        Assert.True(fresh.TryGetCached("Beta", out _));

        File.Delete(XmlPath(fresh, "Alpha"));
        File.Delete(XmlPath(fresh, "Beta"));
        Assert.Equal(GroupLoadStatus.Loaded, fresh.Load("Alpha").Status);
        Assert.Equal(GroupLoadStatus.Loaded, fresh.Load("Beta").Status);
    }

    [Fact]
    public void LoadAll_SkipsCorruptConfig()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);
        WriteConfig(store, "Broken", "not xml");

        var loaded = new GroupStore(_temp.Path).LoadAll();

        var only = Assert.Single(loaded);
        Assert.Equal("Alpha", only.Name);
    }

    [Fact]
    public void ListNames_ReturnsDisplayNamesSorted()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("My Games")).Success);
        Assert.True(store.Save(MakeGroup("Alpha")).Success);

        var names = new GroupStore(_temp.Path).ListNames();

        Assert.Equal(new[] { "Alpha", "My Games" }, names);
    }

    [Fact]
    public void ListNames_MissingRoot_ReturnsEmpty()
    {
        var store = new GroupStore(Path.Combine(_temp.Path, "does-not-exist-yet"));

        Assert.Empty(store.ListNames());
    }

    [Fact]
    public void Delete_RemovesGroupDirectoryAndCache()
    {
        var store = CreateStore();
        Assert.True(store.Save(MakeGroup("Alpha")).Success);
        Assert.Equal(GroupLoadStatus.Loaded, store.Load("Alpha").Status);

        Assert.True(store.Delete("Alpha"));

        Assert.False(Directory.Exists(store.GetGroupDirectory("Alpha")));
        Assert.False(store.TryGetCached("Alpha", out _));
        Assert.Equal(GroupLoadStatus.NotFound, store.Load("Alpha").Status);
        Assert.False(store.Delete("Alpha"));
    }

    [Fact]
    public void Delete_MissingGroup_ReturnsFalseWithoutThrowing()
    {
        var store = CreateStore();

        Assert.False(store.Delete("Missing"));
    }

    private GroupStore CreateStore()
    {
        return new GroupStore(_temp.Path);
    }

    private static string XmlPath(GroupStore store, string name)
    {
        return Path.Combine(store.GetGroupDirectory(name), GroupStore.ConfigFileName);
    }

    private static void WriteConfig(GroupStore store, string name, string xml)
    {
        var xmlPath = XmlPath(store, name);
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
        File.WriteAllText(xmlPath, xml);
    }

    private static Group MakeGroup(string name)
    {
        return new Group
        {
            Name = name,
            Width = 6,
            AllowOpenAll = true,
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

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Dockaroo.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
