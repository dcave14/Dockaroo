using System.Globalization;
using System.Security.Cryptography;
using Dockaroo.Core;
using Dockaroo.Core.Legacy;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class LegacyConfigImporterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string SourceRoot => Path.Combine(_temp.Path, "src");

    private string TargetRoot => Path.Combine(_temp.Path, "target");

    [Fact]
    public void Import_MapsEveryField()
    {
        WriteLegacyGroup(
            "DEV",
            LegacyXml(
                name: "DEV",
                color: "#2A3B4C",
                allowOpenAll: true,
                width: 4,
                opacity: 40,
                shortcuts: LegacyShortcut(@"C:\Tools\Editor.exe", "Editor", "--flag", @"C:\Tools")
                    + LegacyShortcut(@"C:\Games\Game One.exe", "Game One", string.Empty, @"C:\Games")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Null(result.Error);
        var imported = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Imported, imported.Outcome);
        Assert.Equal("DEV", imported.GroupName);
        Assert.Equal(2, imported.ShortcutCount);
        Assert.Equal(2, result.ImportedShortcutCount);

        var loaded = ReadGroup("DEV");
        Assert.Equal("DEV", loaded.Name);
        Assert.Equal("#2A3B4C", loaded.ColorHex);
        Assert.True(loaded.AllowOpenAll);
        Assert.Equal(4, loaded.Width);
        Assert.Equal(40, loaded.Opacity);
        Assert.Equal(2, loaded.Shortcuts.Count);
        Assert.Equal(@"C:\Tools\Editor.exe", loaded.Shortcuts[0].TargetPath);
        Assert.Equal("Editor", loaded.Shortcuts[0].Name);
        Assert.Equal("--flag", loaded.Shortcuts[0].Arguments);
        Assert.Equal(@"C:\Tools", loaded.Shortcuts[0].WorkingDirectory);
        Assert.Equal(@"C:\Games\Game One.exe", loaded.Shortcuts[1].TargetPath);
        Assert.Equal("Game One", loaded.Shortcuts[1].Name);
        Assert.Equal(string.Empty, loaded.Shortcuts[1].Arguments);
        Assert.Equal(@"C:\Games", loaded.Shortcuts[1].WorkingDirectory);
    }

    [Fact]
    public void Import_UnderscoredLegacyName_BecomesSpaces()
    {
        // Real setup: the directory is PC_STUFF and the embedded Name carries the underscore.
        WriteLegacyGroup("PC_STUFF", LegacyXml(name: "PC_STUFF", shortcuts: LegacyShortcut(@"C:\Windows\notepad.exe", "notepad")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal("PC STUFF", result.Groups[0].GroupName);
        var loaded = ReadGroup("PC STUFF");
        Assert.Equal("PC STUFF", loaded.Name);
        Assert.Equal("notepad", loaded.Shortcuts[0].Name);
    }

    [Fact]
    public void Import_NameWithSpaces_IsAcceptedAndDirectoryNameMayDiffer()
    {
        WriteLegacyGroup("My_Games", LegacyXml(name: "My Games", shortcuts: LegacyShortcut(@"C:\Games\Game1.exe", "Game One")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Equal(1, result.ImportedCount);
        var store = new GroupStore(TargetRoot);
        Assert.Equal(new[] { "My Games" }, store.ListNames());
        Assert.True(File.Exists(Path.Combine(store.GetGroupDirectory("My Games"), GroupStore.ConfigFileName)));
    }

    [Fact]
    public void Import_BlankEntryName_FallsBackToTheTargetFileName()
    {
        WriteLegacyGroup("DEV", LegacyXml(name: "DEV", shortcuts: LegacyShortcut(@"C:\Tools\Editor.exe", string.Empty)));

        LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Equal("Editor", ReadGroup("DEV").Shortcuts[0].Name);
    }

    [Fact]
    public void Import_EntryNameLongerThanTheModelLimit_IsTruncated()
    {
        const string longName = "Intel(R) Extreme Tuning Utility";
        Assert.True(longName.Length > ShortcutEntry.MaxNameLength);
        WriteLegacyGroup("PC_STUFF", LegacyXml(name: "PC_STUFF", shortcuts: LegacyShortcut(@"C:\Tools\x.exe", longName)));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(longName[..ShortcutEntry.MaxNameLength], ReadGroup("PC STUFF").Shortcuts[0].Name);
    }

    [Fact]
    public void Import_MissingObjectData_ReturnsPerGroupFailureWithoutThrowing()
    {
        Directory.CreateDirectory(Path.Combine(SourceRoot, "config", "Empty"));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Null(result.Error);
        var failed = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Failed, failed.Outcome);
        Assert.Equal("Empty", failed.GroupName);
        Assert.Contains("ObjectData.xml", failed.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.FailedCount);
        Assert.False(Directory.Exists(TargetRoot));
    }

    [Fact]
    public void Import_CorruptXml_ReturnsPerGroupFailureWithoutThrowing()
    {
        WriteLegacyGroup("Broken", "this is not xml");

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        var failed = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Failed, failed.Outcome);
        Assert.NotNull(failed.Message);
        Assert.Empty(new GroupStore(TargetRoot).ListNames());
    }

    [Fact]
    public void Import_SemanticallyInvalidGroup_ReturnsPerGroupFailureWithoutThrowing()
    {
        // Width 0 fails our model rules (the named colour "red" alone would now import as #FF0000).
        WriteLegacyGroup("Odd", LegacyXml(name: "Odd", color: "red", width: 0, shortcuts: LegacyShortcut(@"C:\a.exe", "a")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        var failed = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Failed, failed.Outcome);
        Assert.Contains("Width", failed.Message, StringComparison.Ordinal);
        Assert.Empty(new GroupStore(TargetRoot).ListNames());
    }

    [Fact]
    public void Import_MissingConfigDirectory_ReportsErrorWithoutThrowing()
    {
        Directory.CreateDirectory(SourceRoot);

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Empty(result.Groups);
        Assert.NotNull(result.Error);
        Assert.Contains("config", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_NameThatNormalisesToBlank_FailsWithoutThrowingAndOtherGroupsStillImport()
    {
        WriteLegacyGroup("BLANK", LegacyXml(name: "___", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));
        WriteLegacyGroup("GOOD", LegacyXml(name: "GOOD", shortcuts: LegacyShortcut(@"C:\b.exe", "b")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.ImportedCount);
        var failed = Assert.Single(result.Groups, group => group.Outcome == LegacyImportOutcome.Failed);
        Assert.Equal("BLANK", failed.GroupName);
        Assert.Contains("cannot be used as a group name", failed.Message, StringComparison.Ordinal);
        var imported = Assert.Single(result.Groups, group => group.Outcome == LegacyImportOutcome.Imported);
        Assert.Equal("GOOD", imported.GroupName);
        Assert.Equal(new[] { "GOOD" }, new GroupStore(TargetRoot).ListNames());
    }

    [Fact]
    public void Import_NameWithInvalidCharacters_FailsWithoutThrowing()
    {
        WriteLegacyGroup("BAD", LegacyXml(name: "My-Group!", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        var failed = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Failed, failed.Outcome);
        Assert.Equal("My-Group!", failed.GroupName);
        Assert.Contains("cannot be used as a group name", failed.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(TargetRoot));
    }

    [Fact]
    public void Import_DirectoryOfGarbageFilesAndBadSourcePaths_NeverThrows()
    {
        var config = Path.Combine(SourceRoot, "config");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(Path.Combine(config, "NoXml"));

        var corrupt = Directory.CreateDirectory(Path.Combine(config, "Corrupt"));
        File.WriteAllText(Path.Combine(corrupt.FullName, "ObjectData.xml"), "@@@ not xml @@@");

        var empty = Directory.CreateDirectory(Path.Combine(config, "EmptyFile"));
        File.WriteAllText(Path.Combine(empty.FullName, "ObjectData.xml"), string.Empty);

        var wrongRoot = Directory.CreateDirectory(Path.Combine(config, "WrongRoot"));
        File.WriteAllText(Path.Combine(wrongRoot.FullName, "ObjectData.xml"), "<Nope><Width>6</Width></Nope>");

        var missingWidth = Directory.CreateDirectory(Path.Combine(config, "MissingWidth"));
        File.WriteAllText(Path.Combine(missingWidth.FullName, "ObjectData.xml"), "<Category><Name>MissingWidth</Name><Opacity>10</Opacity></Category>");

        File.WriteAllText(Path.Combine(config, "stray.txt"), "not a group directory");

        var store = new GroupStore(TargetRoot);
        var result = LegacyConfigImporter.Import(SourceRoot, store);

        Assert.Null(result.Error);
        Assert.Equal(5, result.Groups.Count);
        Assert.All(result.Groups, group => Assert.Equal(LegacyImportOutcome.Failed, group.Outcome));
        Assert.All(result.Groups, group => Assert.NotNull(group.Message));

        // A source that is a file, and a blank source, also come back as results instead of throws.
        var fileResult = LegacyConfigImporter.Import(Path.Combine(config, "stray.txt"), store);
        Assert.Empty(fileResult.Groups);
        Assert.NotNull(fileResult.Error);

        var blankResult = LegacyConfigImporter.Import(" ", store);
        Assert.Empty(blankResult.Groups);
        Assert.NotNull(blankResult.Error);

        Assert.False(Directory.Exists(TargetRoot));
    }

    [Fact]
    public void Import_ExistingGroup_IsSkippedAndNeverOverwritten()
    {
        var store = new GroupStore(TargetRoot);
        var existing = new Group { Name = "Alpha", Width = 3, Opacity = 10, ColorHex = "#123456" };
        existing.Shortcuts.Add(new ShortcutEntry { TargetPath = @"C:\keep.exe", Name = "keep" });
        Assert.True(store.Save(existing).Success);
        var configFile = Path.Combine(store.GetGroupDirectory("Alpha"), GroupStore.ConfigFileName);
        var bytesBefore = File.ReadAllBytes(configFile);

        WriteLegacyGroup("Alpha", LegacyXml(name: "Alpha", width: 5, shortcuts: LegacyShortcut(@"C:\new.exe", "new")));

        var result = LegacyConfigImporter.Import(SourceRoot, store);

        var skipped = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Skipped, skipped.Outcome);
        Assert.Contains("already exists", skipped.Message, StringComparison.Ordinal);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.ImportedCount);

        Assert.Equal(bytesBefore, File.ReadAllBytes(configFile));
        Assert.Equal(3, ReadGroup("Alpha").Width);
    }

    [Fact]
    public void Import_RunningTwice_SkipsEverythingTheSecondTime()
    {
        WriteLegacyGroup("DEV", LegacyXml(name: "DEV", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));
        WriteLegacyGroup("GAMING", LegacyXml(name: "GAMING", shortcuts: LegacyShortcut(@"C:\b.exe", "b")));
        var store = new GroupStore(TargetRoot);

        var first = LegacyConfigImporter.Import(SourceRoot, store);
        var second = LegacyConfigImporter.Import(SourceRoot, store);

        Assert.Equal(2, first.ImportedCount);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(2, second.SkippedCount);
        Assert.All(second.Groups, group => Assert.Contains("already exists", group.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Import_NamedLegacyColour_BecomesHex()
    {
        // The legacy editor wrote ColorTranslator.ToHtml output, which is a name for known colours.
        WriteLegacyGroup("DEV", LegacyXml(name: "DEV", color: "White", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        var imported = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Imported, imported.Outcome);
        Assert.Null(imported.Message);
        Assert.Equal("#FFFFFF", ReadGroup("DEV").ColorHex);
    }

    [Fact]
    public void Import_UnparseableLegacyColour_FallsBackToDarkAndReportsIt()
    {
        WriteLegacyGroup("DEV", LegacyXml(name: "DEV", color: "NotAColour", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        var imported = Assert.Single(result.Groups);
        Assert.Equal(LegacyImportOutcome.Imported, imported.Outcome);
        Assert.Contains("'NotAColour' is not a colour", imported.Message, StringComparison.Ordinal);
        Assert.Equal(Group.DarkColorHex, ReadGroup("DEV").ColorHex);
    }

    [Fact]
    public void Import_CopiesGroupIconAndImageWithContentPreserved()
    {
        var sourceDirectory = WriteLegacyGroup("DEV", LegacyXml(name: "DEV", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));
        var icon = new byte[] { 1, 2, 3, 4 };
        var image = new byte[] { 9, 8, 7 };
        File.WriteAllBytes(Path.Combine(sourceDirectory, "GroupIcon.ico"), icon);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "GroupImage.png"), image);
        var store = new GroupStore(TargetRoot);

        var result = LegacyConfigImporter.Import(SourceRoot, store);
        var targetDirectory = store.GetGroupDirectory("DEV");

        Assert.Equal(1, result.ImportedCount);
        Assert.Null(result.Groups[0].Message);
        Assert.Equal(icon, File.ReadAllBytes(Path.Combine(targetDirectory, "GroupIcon.ico")));
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(targetDirectory, "GroupImage.png")));
    }

    [Fact]
    public void Import_WritesOnlyUnderTheTargetRootAndLeavesTheSourceUntouched()
    {
        WriteLegacyGroup("DEV", LegacyXml(name: "DEV", shortcuts: LegacyShortcut(@"C:\a.exe", "a")));
        WriteLegacyGroup("PC_STUFF", LegacyXml(name: "PC_STUFF", shortcuts: LegacyShortcut(@"C:\b.exe", "b")));
        File.WriteAllBytes(Path.Combine(SourceRoot, "config", "DEV", "GroupIcon.ico"), new byte[] { 5, 6 });
        var sourceBefore = SnapshotFiles(SourceRoot);

        var result = LegacyConfigImporter.Import(SourceRoot, new GroupStore(TargetRoot));

        Assert.Equal(2, result.ImportedCount);

        var targetPrefix = TargetRoot + Path.DirectorySeparatorChar;
        var sourcePrefix = SourceRoot + Path.DirectorySeparatorChar;
        foreach (var entry in Directory.EnumerateFileSystemEntries(_temp.Path, "*", SearchOption.AllDirectories))
        {
            Assert.True(
                entry.Equals(TargetRoot, StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase)
                    || entry.Equals(SourceRoot, StringComparison.OrdinalIgnoreCase)
                    || entry.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase),
                $"The importer wrote outside its target root: {entry}");
        }

        Assert.Equal(2, Directory.GetDirectories(TargetRoot).Length);
        Assert.Empty(Directory.GetFiles(TargetRoot, "*.tmp", SearchOption.AllDirectories));
        Assert.Equal(sourceBefore, SnapshotFiles(SourceRoot));
    }

    [RealSetupFact]
    public void Import_RealLegacySetup_ImportsAllGroupsWithValidEntriesAndLeavesTheSourceUntouched()
    {
        var realLegacyRoot = RealSetupFactAttribute.RealLegacyRoot;
        var sourceBefore = HashLegacyConfigs(realLegacyRoot);
        Assert.Equal(6, sourceBefore.Count);

        var result = LegacyConfigImporter.Import(realLegacyRoot, new GroupStore(TargetRoot));

        Assert.Null(result.Error);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(6, result.ImportedCount);
        Assert.Equal(35, result.ImportedShortcutCount);

        var store = new GroupStore(TargetRoot);
        var names = store.ListNames();
        Assert.Equal(6, names.Count);
        Assert.Contains("PC STUFF", names);

        var totalEntries = 0;
        foreach (var name in names)
        {
            var loaded = ReadGroup(name);
            Assert.True(loaded.TryValidate(out var validationError), validationError);
            totalEntries += loaded.Shortcuts.Count;
        }

        Assert.Equal(35, totalEntries);
        Assert.Equal(sourceBefore, HashLegacyConfigs(realLegacyRoot));
    }

    private string WriteLegacyGroup(string directoryName, string xml)
    {
        var directory = Path.Combine(SourceRoot, "config", directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ObjectData.xml"), xml);
        return directory;
    }

    private Group ReadGroup(string name)
    {
        // A fresh store: reads go to disk, proving the import wrote loadable configs.
        var result = new GroupStore(TargetRoot).Load(name);
        Assert.Equal(GroupLoadStatus.Loaded, result.Status);
        Assert.Null(result.Error);
        return Assert.IsType<Group>(result.Group);
    }

    private static Dictionary<string, string> SnapshotFiles(string root)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            snapshot[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return snapshot;
    }

    private static Dictionary<string, string> HashLegacyConfigs(string legacyRoot)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(legacyRoot, "config"), "ObjectData.xml", SearchOption.AllDirectories))
        {
            hashes[Path.GetRelativePath(legacyRoot, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        }

        return hashes;
    }

    private static string LegacyXml(
        string name,
        string color = "#1F1F1F",
        bool allowOpenAll = false,
        int width = 6,
        double opacity = 10,
        string shortcuts = "")
    {
        return $"""
<?xml version="1.0"?>
<Category xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Name>{name}</Name>
  <ColorString>{color}</ColorString>
  <allowOpenAll>{(allowOpenAll ? "true" : "false")}</allowOpenAll>
  <ShortcutList>
{shortcuts}
  </ShortcutList>
  <Width>{width}</Width>
  <Opacity>{opacity.ToString(CultureInfo.InvariantCulture)}</Opacity>
</Category>
""";
    }

    private static string LegacyShortcut(string filePath, string name, string arguments = "", string workingDirectory = "")
    {
        return $"""
    <ProgramShortcut>
      <Arguments>{arguments}</Arguments>
      <WorkingDirectory>{workingDirectory}</WorkingDirectory>
      <FilePath>{filePath}</FilePath>
      <isWindowsApp>false</isWindowsApp>
      <name>{name}</name>
    </ProgramShortcut>
""";
    }
}

/// <summary>
/// Runs the live-data acceptance test only when the real legacy setup is present; otherwise
/// the test is reported as skipped with a message that names the missing folder.
/// </summary>
internal sealed class RealSetupFactAttribute : FactAttribute
{
    /// <summary>Environment variable naming the user's real legacy-app folder (read-only source).</summary>
    internal const string RealLegacyRootVariable = "DOCKAROO_LEGACY_ROOT";

    internal static string RealLegacyRoot => Environment.GetEnvironmentVariable(RealLegacyRootVariable) ?? string.Empty;

    public RealSetupFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(RealLegacyRoot) || !Directory.Exists(Path.Combine(RealLegacyRoot, "config")))
        {
            Skip = $"{RealLegacyRootVariable} does not name a legacy setup with a config folder; skipping the live-data acceptance test.";
        }
    }
}
