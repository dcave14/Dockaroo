using System.Security.Cryptography;
using Dockaroo.Core;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class DataRootResolverTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Resolve_WithoutPortableMarker_UsesDockarooUnderLocalAppData()
    {
        var exeDirectory = MakeDirectory("exe");
        var localAppData = MakeDirectory("localappdata");

        var root = DataRootResolver.Resolve(exeDirectory, localAppData);

        Assert.Equal(Path.Combine(localAppData, "Dockaroo"), root);
        Assert.StartsWith(localAppData, root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WithPortableMarker_ReturnsTheExeDirectory()
    {
        var exeDirectory = MakeDirectory("exe");
        File.WriteAllText(Path.Combine(exeDirectory, "portable.txt"), string.Empty);
        var localAppData = MakeDirectory("localappdata");

        var root = DataRootResolver.Resolve(exeDirectory, localAppData);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(exeDirectory),
            Path.TrimEndingDirectorySeparator(root));
    }

    [Fact]
    public void Subfolders_AreDerivedFromTheRoot()
    {
        var resolver = NewResolver(out _);

        Assert.Equal(Path.Combine(resolver.RootDirectory, "config"), resolver.ConfigDirectory);
        Assert.Equal(Path.Combine(resolver.RootDirectory, "icon-cache"), resolver.IconCacheDirectory);
        Assert.Equal(Path.Combine(resolver.RootDirectory, "Shortcuts"), resolver.ShortcutsDirectory);
    }

    [Fact]
    public void MigrateLegacyData_CopiesGroupFoldersAndShortcutsOnce()
    {
        var resolver = NewResolver(out var directories);
        WriteFile(Path.Combine(directories.Exe, "config", "GroupA", "group.xml"), "<Group>A</Group>");
        WriteFile(Path.Combine(directories.Exe, "config", "GroupA", "Icons", "icon.png"), "icon-bytes");
        WriteFile(Path.Combine(directories.Exe, "Shortcuts", "GroupA.lnk"), "lnk-a");
        WriteFile(Path.Combine(directories.Exe, "Shortcuts", "GroupB.lnk"), "lnk-b");
        WriteFile(Path.Combine(directories.Exe, "Shortcuts", "notes.txt"), "not-a-shortcut");

        resolver.MigrateLegacyData();

        Assert.Equal("<Group>A</Group>", File.ReadAllText(Path.Combine(resolver.ConfigDirectory, "GroupA", "group.xml")));
        Assert.Equal("icon-bytes", File.ReadAllText(Path.Combine(resolver.ConfigDirectory, "GroupA", "Icons", "icon.png")));
        Assert.Equal("lnk-a", File.ReadAllText(Path.Combine(resolver.ShortcutsDirectory, "GroupA.lnk")));
        Assert.Equal("lnk-b", File.ReadAllText(Path.Combine(resolver.ShortcutsDirectory, "GroupB.lnk")));
        Assert.False(File.Exists(Path.Combine(resolver.ShortcutsDirectory, "notes.txt")));

        // Copy, never move: the legacy files stay where they were.
        Assert.True(File.Exists(Path.Combine(directories.Exe, "config", "GroupA", "group.xml")));
        Assert.True(File.Exists(Path.Combine(directories.Exe, "Shortcuts", "GroupA.lnk")));
    }

    [Fact]
    public void MigrateLegacyData_NeverOverwritesAnExistingDestinationFile()
    {
        var resolver = NewResolver(out var directories);
        WriteFile(Path.Combine(directories.Exe, "config", "GroupA", "group.xml"), "legacy-xml");
        WriteFile(Path.Combine(directories.Exe, "config", "GroupA", "readme.txt"), "legacy-readme");
        WriteFile(Path.Combine(directories.Exe, "Shortcuts", "GroupA.lnk"), "legacy-lnk");

        // The destination group folder exists with an edited file but no group.xml yet, so the
        // migration still runs and must leave the edited file byte-identical. The same for the
        // already-present shortcut.
        WriteFile(Path.Combine(resolver.ConfigDirectory, "GroupA", "readme.txt"), "edited-destination");
        WriteFile(Path.Combine(resolver.ShortcutsDirectory, "GroupA.lnk"), "edited-lnk");

        resolver.MigrateLegacyData();

        Assert.Equal("edited-destination", File.ReadAllText(Path.Combine(resolver.ConfigDirectory, "GroupA", "readme.txt")));
        Assert.Equal("edited-lnk", File.ReadAllText(Path.Combine(resolver.ShortcutsDirectory, "GroupA.lnk")));

        // Missing files are still copied next to the surviving ones.
        Assert.Equal("legacy-xml", File.ReadAllText(Path.Combine(resolver.ConfigDirectory, "GroupA", "group.xml")));
        Assert.Equal("legacy-readme", File.ReadAllText(Path.Combine(directories.Exe, "config", "GroupA", "readme.txt")));
    }

    [Fact]
    public void MigrateLegacyData_SecondRun_ChangesNothing()
    {
        var resolver = NewResolver(out var directories);
        WriteFile(Path.Combine(directories.Exe, "config", "GroupA", "group.xml"), "<Group>A</Group>");
        WriteFile(Path.Combine(directories.Exe, "config", "GroupA", "Icons", "icon.png"), "icon-bytes");
        WriteFile(Path.Combine(directories.Exe, "Shortcuts", "GroupA.lnk"), "lnk-a");

        resolver.MigrateLegacyData();
        var before = Snapshot(resolver.RootDirectory);

        resolver.MigrateLegacyData();
        var after = Snapshot(resolver.RootDirectory);

        AssertSameSnapshot(before, after);
    }

    [Fact]
    public void MigrateLegacyData_WhenTheRootAlreadyHasAGroup_CopiesNothing()
    {
        var resolver = NewResolver(out var directories);
        WriteFile(Path.Combine(directories.Exe, "config", "LegacyGroup", "group.xml"), "legacy");
        WriteFile(Path.Combine(directories.Exe, "Shortcuts", "LegacyGroup.lnk"), "legacy-lnk");
        WriteFile(Path.Combine(resolver.ConfigDirectory, "ExistingGroup", "group.xml"), "existing");

        resolver.MigrateLegacyData();

        Assert.False(Directory.Exists(Path.Combine(resolver.ConfigDirectory, "LegacyGroup")));
        Assert.False(File.Exists(Path.Combine(resolver.ShortcutsDirectory, "LegacyGroup.lnk")));
        Assert.Equal("existing", File.ReadAllText(Path.Combine(resolver.ConfigDirectory, "ExistingGroup", "group.xml")));
    }

    [Fact]
    public void MigrateLegacyData_InPortableMode_IsANoOp()
    {
        var exeDirectory = MakeDirectory("exe");
        File.WriteAllText(Path.Combine(exeDirectory, "portable.txt"), string.Empty);
        WriteFile(Path.Combine(exeDirectory, "config", "GroupA", "group.xml"), "portable-data");
        var before = Snapshot(exeDirectory);

        var resolver = new DataRootResolver(exeDirectory, exeDirectory);
        resolver.MigrateLegacyData();

        var after = Snapshot(exeDirectory);
        AssertSameSnapshot(before, after);
        Assert.Equal("portable-data", File.ReadAllText(Path.Combine(exeDirectory, "config", "GroupA", "group.xml")));
    }

    private DataRootResolver NewResolver(out (string Exe, string LocalAppData) directories)
    {
        directories = (MakeDirectory("exe"), MakeDirectory("localappdata"));
        var root = DataRootResolver.Resolve(directories.Exe, directories.LocalAppData);
        return new DataRootResolver(root, directories.Exe);
    }

    private string MakeDirectory(string name)
    {
        var path = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    /// <summary>Relative path to content hash for every file under <paramref name="root"/>.</summary>
    private static Dictionary<string, string> Snapshot(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(root, file)] =
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return snapshot;
    }

    /// <summary>Asserts the same file set with the same content hashes: nothing added, removed or edited.</summary>
    private static void AssertSameSnapshot(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        Assert.Equal(before.Count, after.Count);
        foreach (var (relativePath, contentHash) in before)
        {
            Assert.True(after.TryGetValue(relativePath, out var afterHash), $"'{relativePath}' disappeared.");
            Assert.Equal(contentHash, afterHash);
        }
    }
}
