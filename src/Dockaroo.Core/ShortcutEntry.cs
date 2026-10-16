using System.Xml.Serialization;

namespace Dockaroo.Core;

/// <summary>
/// One launchable entry inside a group. Field semantics are ported from the
/// legacy client.Classes.ProgramShortcut (legacy-app Classes/ProgramShortcut.cs).
/// </summary>
[XmlType("Shortcut")]
public class ShortcutEntry
{
    /// <summary>Maximum length of <see cref="Name"/> (legacy UI limit).</summary>
    public const int MaxNameLength = 27;

    /// <summary>Full path of the target .exe/.lnk (or other shell target).</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Text shown in the popup; defaults to the target file name without extension.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Extra command line arguments; empty means none.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Working directory used when launching the target.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Creates an entry for a target path, deriving name and working directory from it.</summary>
    public static ShortcutEntry Create(string targetPath)
    {
        return new ShortcutEntry
        {
            TargetPath = targetPath,
            Name = DefaultNameFor(targetPath),
            WorkingDirectory = DefaultWorkingDirectoryFor(targetPath),
        };
    }

    /// <summary>Target file name without extension, truncated to <see cref="MaxNameLength"/>.</summary>
    public static string DefaultNameFor(string targetPath)
    {
        var name = Path.GetFileNameWithoutExtension(targetPath) ?? string.Empty;
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    /// <summary>
    /// Default working directory: the target directory itself for folders,
    /// otherwise the directory the target file lives in.
    /// </summary>
    public static string DefaultWorkingDirectoryFor(string targetPath)
    {
        if (string.IsNullOrEmpty(targetPath))
        {
            return string.Empty;
        }

        if (Directory.Exists(targetPath))
        {
            return targetPath;
        }

        return Path.GetDirectoryName(targetPath) ?? string.Empty;
    }

    /// <summary>
    /// Independent copy of this entry. Every field is an immutable string, so the copy shares
    /// nothing mutable with the original and can be handed to callers that may edit it.
    /// </summary>
    public ShortcutEntry Clone()
    {
        return (ShortcutEntry)MemberwiseClone();
    }

    /// <summary>Checks required fields and the 27 character name limit; never throws.</summary>
    public bool TryValidate(out string? error)
    {
        if (string.IsNullOrWhiteSpace(TargetPath))
        {
            error = "TargetPath is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "Name is required.";
            return false;
        }

        if (Name.Length > MaxNameLength)
        {
            error = $"Name must be at most {MaxNameLength} characters.";
            return false;
        }

        error = null;
        return true;
    }
}
