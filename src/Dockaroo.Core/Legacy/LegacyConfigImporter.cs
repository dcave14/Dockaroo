using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;

namespace Dockaroo.Core.Legacy;

/// <summary>Outcome of importing one legacy group directory.</summary>
public enum LegacyImportOutcome
{
    /// <summary>The group was converted and saved into the target store.</summary>
    Imported,

    /// <summary>A group with the same name already exists in the target; nothing was changed.</summary>
    Skipped,

    /// <summary>The directory could not be converted; see <see cref="LegacyGroupImport.Message"/>.</summary>
    Failed,
}

/// <summary>Result of importing one legacy group directory. Always returned, never thrown.</summary>
public sealed record LegacyGroupImport(
    string DirectoryName,
    string GroupName,
    LegacyImportOutcome Outcome,
    int ShortcutCount,
    string? Message);

/// <summary>Result of importing a whole legacy root. Always returned, never thrown.</summary>
public sealed record LegacyImportResult(
    string SourceRoot,
    string TargetRoot,
    IReadOnlyList<LegacyGroupImport> Groups,
    string? Error)
{
    /// <summary>Number of groups saved into the target store.</summary>
    public int ImportedCount => Groups.Count(group => group.Outcome == LegacyImportOutcome.Imported);

    /// <summary>Number of groups skipped because the name already existed in the target.</summary>
    public int SkippedCount => Groups.Count(group => group.Outcome == LegacyImportOutcome.Skipped);

    /// <summary>Number of group directories that could not be converted.</summary>
    public int FailedCount => Groups.Count(group => group.Outcome == LegacyImportOutcome.Failed);

    /// <summary>Total shortcuts across the imported groups.</summary>
    public int ImportedShortcutCount => Groups
        .Where(group => group.Outcome == LegacyImportOutcome.Imported)
        .Sum(group => group.ShortcutCount);
}

/// <summary>
/// Converts a legacy-app setup (the original app this one replaces) into our config
/// layout. The legacy root is the old app's directory: every group is stored as
/// <c>&lt;root&gt;/config/&lt;Directory&gt;/ObjectData.xml</c> (legacy-app Classes/Category.cs,
/// legacy-app Classes/ProgramShortcut.cs) and is written through
/// <see cref="GroupStore.Save"/> as <c>&lt;GroupStore root&gt;/&lt;Name&gt;/group.xml</c>,
/// so the target keeps the store's copy-on-write and per-name serialization guarantees.
/// <c>GroupIcon.ico</c> and <c>GroupImage.png</c> next to the legacy config are copied
/// into the target group directory unchanged.
/// <para>
/// The embedded Name element is the display name, normalised for our model where the
/// legacy format disagrees: runs of underscores become a single space (the old editor
/// did the same when naming the group .lnk, so the real directory PC_STUFF with embedded
/// Name "PC_STUFF" becomes "PC STUFF"), and entry names are truncated to the 27
/// character limit. The legacy Opacity is already percent transparent like ours, so it
/// is copied; the isWindowsApp flag is not part of our model and is ignored.
/// </para>
/// <para>
/// Import never overwrites: a group whose name already exists in the target is skipped
/// and reported. Per-group problems (missing or corrupt XML, a name or config our model
/// rejects) come back as <see cref="LegacyImportOutcome.Failed"/> entries instead of
/// exceptions. The source tree is only ever read.
/// </para>
/// </summary>
public static class LegacyConfigImporter
{
    /// <summary>Directory under the legacy root that holds one directory per group.</summary>
    public const string LegacyConfigDirectoryName = "config";

    /// <summary>Per-group config file name of the legacy app.</summary>
    public const string LegacyConfigFileName = "ObjectData.xml";

    /// <summary>Legacy group icon file copied into the target group directory.</summary>
    public const string GroupIconFileName = "GroupIcon.ico";

    /// <summary>Legacy group image file copied into the target group directory.</summary>
    public const string GroupImageFileName = "GroupImage.png";

    private static readonly XmlSerializer Serializer = new(typeof(LegacyCategoryDto));
    private static readonly Regex UnderscorePattern = new("(_)+", RegexOptions.Compiled);

    /// <summary>
    /// Imports every group found under <paramref name="sourceRoot"/> (the old app's
    /// directory: <c>&lt;sourceRoot&gt;/config/&lt;Directory&gt;/ObjectData.xml</c>) into
    /// <paramref name="store"/>. Never throws for missing, corrupt or rejected configs;
    /// each directory is reported in the result.
    /// </summary>
    public static LegacyImportResult Import(string sourceRoot, GroupStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            return new LegacyImportResult(
                string.Empty,
                store.RootDirectory,
                Array.Empty<LegacyGroupImport>(),
                "A source root directory is required.");
        }

        string root;
        try
        {
            root = Path.GetFullPath(sourceRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new LegacyImportResult(
                sourceRoot,
                store.RootDirectory,
                Array.Empty<LegacyGroupImport>(),
                $"'{sourceRoot}' is not a usable folder path: {ex.Message}");
        }

        var configDirectory = Path.Combine(root, LegacyConfigDirectoryName);
        if (!Directory.Exists(configDirectory))
        {
            return new LegacyImportResult(
                root,
                store.RootDirectory,
                Array.Empty<LegacyGroupImport>(),
                $"No '{LegacyConfigDirectoryName}' directory was found under '{root}'. Select the folder of the old app installation.");
        }

        var groups = new List<LegacyGroupImport>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(configDirectory).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                groups.Add(ImportGroup(directory, store));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LegacyImportResult(root, store.RootDirectory, groups, ex.Message);
        }

        return new LegacyImportResult(root, store.RootDirectory, groups, null);
    }

    private static LegacyGroupImport ImportGroup(string sourceGroupDirectory, GroupStore store)
    {
        var directoryName = Path.GetFileName(sourceGroupDirectory);
        var fallbackName = ToDisplayName(directoryName);
        var xmlFile = Path.Combine(sourceGroupDirectory, LegacyConfigFileName);
        if (!File.Exists(xmlFile))
        {
            return Failed(directoryName, fallbackName, $"No {LegacyConfigFileName} was found in this directory.");
        }

        LegacyCategoryDto? dto;
        try
        {
            using var stream = File.OpenRead(xmlFile);
            dto = (LegacyCategoryDto?)Serializer.Deserialize(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException or FormatException or OverflowException)
        {
            return Failed(directoryName, fallbackName, $"The legacy config could not be read: {ex.Message}");
        }

        if (dto is null)
        {
            return Failed(directoryName, fallbackName, "The legacy config contained no group.");
        }

        var name = string.IsNullOrWhiteSpace(dto.Name) ? fallbackName : ToDisplayName(dto.Name);
        if (!Group.IsValidName(name))
        {
            var raw = string.IsNullOrWhiteSpace(dto.Name) ? directoryName : dto.Name;
            return Failed(
                directoryName,
                string.IsNullOrWhiteSpace(name) ? directoryName : name,
                $"The legacy name '{raw}' cannot be used as a group name: it must be 1 to {Group.MaxNameLength} characters of letters, digits and spaces after underscores become spaces.");
        }

        var group = Map(dto, name);
        if (!group.TryValidate(out var validationError))
        {
            return Failed(directoryName, name, validationError!, group.Shortcuts.Count);
        }

        var targetConfigFile = Path.Combine(store.GetGroupDirectory(name), GroupStore.ConfigFileName);
        if (File.Exists(targetConfigFile))
        {
            return Skipped(directoryName, name);
        }

        var save = store.Save(group);
        if (!save.Success)
        {
            // The store reports a name conflict as a failed save; a config that exists now
            // is that conflict (also when another writer created it after our pre-check).
            return File.Exists(targetConfigFile)
                ? Skipped(directoryName, name)
                : Failed(directoryName, name, save.Error ?? "The group could not be saved.", group.Shortcuts.Count);
        }

        var iconWarning = CopyIcons(sourceGroupDirectory, store.GetGroupDirectory(name));
        var colorWarning = ColorWarning(dto.ColorString);
        var warning = colorWarning is null ? iconWarning
            : iconWarning is null ? colorWarning
            : colorWarning + " " + iconWarning;
        return new LegacyGroupImport(directoryName, name, LegacyImportOutcome.Imported, group.Shortcuts.Count, warning);
    }

    // The legacy app wrote ColorTranslator.ToHtml output, so named colours ("White") occur.
    private static string MapColor(string? colorString)
    {
        Group.TryNormalizeColorHex(colorString, out var hex);
        return hex;
    }

    private static string? ColorWarning(string? colorString)
    {
        return string.IsNullOrWhiteSpace(colorString) || Group.TryNormalizeColorHex(colorString, out _)
            ? null
            : $"The legacy colour '{colorString}' is not a colour; the default dark colour is used.";
    }

    private static Group Map(LegacyCategoryDto dto, string name)
    {
        var group = new Group
        {
            Name = name,
            Width = dto.Width,
            AllowOpenAll = dto.AllowOpenAll,
            ColorHex = MapColor(dto.ColorString),
            Opacity = (int)Math.Round(dto.Opacity, MidpointRounding.AwayFromZero),
        };

        foreach (var legacy in dto.ShortcutList ?? new List<LegacyShortcutDto>())
        {
            if (legacy is null)
            {
                continue;
            }

            var targetPath = legacy.FilePath ?? string.Empty;
            group.Shortcuts.Add(new ShortcutEntry
            {
                TargetPath = targetPath,
                Name = EntryName(legacy.Name, targetPath),
                Arguments = legacy.Arguments ?? string.Empty,
                WorkingDirectory = legacy.WorkingDirectory ?? string.Empty,
            });
        }

        return group;
    }

    /// <summary>
    /// Our model requires an entry name and caps it at 27 characters, both of which the
    /// legacy format does not enforce (the real setup contains "Intel(R) Extreme Tuning
    /// Utility"). A blank legacy name falls back to the target file name, the same
    /// default <see cref="ShortcutEntry.Create"/> uses.
    /// </summary>
    private static string EntryName(string? legacyName, string targetPath)
    {
        var name = string.IsNullOrWhiteSpace(legacyName) ? ShortcutEntry.DefaultNameFor(targetPath) : legacyName;
        return name.Length <= ShortcutEntry.MaxNameLength ? name : name[..ShortcutEntry.MaxNameLength];
    }

    /// <summary>
    /// Legacy names may contain underscores (the old editor turned every run of them into
    /// a space when it built the group .lnk name; legacy-app Classes/Category.cs:122), which
    /// our name rules do not allow. This is the same conversion the old app applied.
    /// </summary>
    private static string ToDisplayName(string name)
    {
        return UnderscorePattern.Replace(name, " ").Trim();
    }

    /// <summary>Copies the legacy group assets into the target group directory; returns a warning when a copy fails.</summary>
    private static string? CopyIcons(string sourceGroupDirectory, string targetGroupDirectory)
    {
        List<string>? failures = null;
        foreach (var fileName in new[] { GroupIconFileName, GroupImageFileName })
        {
            var sourceFile = Path.Combine(sourceGroupDirectory, fileName);
            if (!File.Exists(sourceFile))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(targetGroupDirectory);
                File.Copy(sourceFile, Path.Combine(targetGroupDirectory, fileName), overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures ??= new List<string>();
                failures.Add($"'{fileName}' could not be copied ({ex.Message})");
            }
        }

        return failures is null ? null : $"The group was imported, but {string.Join("; ", failures)}.";
    }

    private static LegacyGroupImport Failed(string directoryName, string groupName, string message, int shortcutCount = 0)
    {
        return new LegacyGroupImport(directoryName, groupName, LegacyImportOutcome.Failed, shortcutCount, message);
    }

    private static LegacyGroupImport Skipped(string directoryName, string groupName)
    {
        return new LegacyGroupImport(
            directoryName,
            groupName,
            LegacyImportOutcome.Skipped,
            0,
            $"A group named '{groupName}' already exists.");
    }

    // Stock legacy XmlSerializer shape: root <Category> with Name, ColorString,
    // allowOpenAll, ShortcutList/ProgramShortcut (member order Arguments, WorkingDirectory,
    // FilePath, isWindowsApp, name), Width and Opacity.
    /// <summary>Wire shape of the legacy <c>client.Classes.Category</c> config.</summary>
    [XmlRoot("Category")]
    public sealed class LegacyCategoryDto
    {
        [XmlElement("Name")]
        public string? Name { get; set; }

        [XmlElement("ColorString")]
        public string? ColorString { get; set; }

        [XmlElement("allowOpenAll")]
        public bool AllowOpenAll { get; set; }

        [XmlArray("ShortcutList")]
        [XmlArrayItem("ProgramShortcut")]
        public List<LegacyShortcutDto>? ShortcutList { get; set; }

        [XmlElement("Width")]
        public int Width { get; set; }

        [XmlElement("Opacity")]
        public double Opacity { get; set; }
    }

    /// <summary>Wire shape of the legacy <c>client.Classes.ProgramShortcut</c> entry.</summary>
    public sealed class LegacyShortcutDto
    {
        [XmlElement("Arguments")]
        public string? Arguments { get; set; }

        [XmlElement("WorkingDirectory")]
        public string? WorkingDirectory { get; set; }

        [XmlElement("FilePath")]
        public string? FilePath { get; set; }

        [XmlElement("isWindowsApp")]
        public bool IsWindowsApp { get; set; }

        [XmlElement("name")]
        public string? Name { get; set; }
    }
}
