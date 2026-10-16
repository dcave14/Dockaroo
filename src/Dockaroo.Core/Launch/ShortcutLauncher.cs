using System.Diagnostics;
using Dockaroo.Core.Icons;

namespace Dockaroo.Core.Launch;

/// <summary>Outcome of one launch attempt; <see cref="Error"/> carries the reason on failure.</summary>
public sealed record LaunchResult(bool Started, string? Error)
{
    /// <summary>A process was started.</summary>
    public static LaunchResult Ok() => new(true, null);

    /// <summary>Nothing was started; <paramref name="error"/> says why.</summary>
    public static LaunchResult Failed(string error) => new(false, error);
}

/// <summary>
/// Launches one <see cref="ShortcutEntry"/> with the legacy client semantics
/// (legacy-app Forms/frmMain.cs <c>OpenFile</c> and legacy-app User controls/ucShortcut.cs
/// <c>ucShortcut_Click</c>): target path, arguments and working directory are handed to the
/// shell. <see cref="Launch"/> never throws; every failure comes back as a
/// <see cref="LaunchResult"/>.
/// </summary>
public static class ShortcutLauncher
{
    private const string AppsFolderPrefix = @"shell:appsFolder\";

    /// <summary>
    /// Builds the start info for an entry: the target path as file name, the entry's
    /// arguments, and the entry's working directory. A missing or whitespace working
    /// directory falls back to the target's own directory (the legacy new-entry default).
    /// Shell execution is enabled so .lnk files and shell aliases start like they did in the
    /// legacy .NET Framework client, where shell execution was the default; MS Store app ids
    /// go through the apps-folder alias exactly like the legacy control did.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(ShortcutEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new ProcessStartInfo
        {
            FileName = GetFileName(entry.TargetPath),
            Arguments = entry.Arguments ?? string.Empty,
            WorkingDirectory = GetWorkingDirectory(entry),
            UseShellExecute = true,
        };
    }

    /// <summary>
    /// Starts the entry without waiting for it. Invalid entries and start failures return a
    /// failure result instead of throwing.
    /// </summary>
    public static LaunchResult Launch(ShortcutEntry? entry)
    {
        if (entry is null)
        {
            return LaunchResult.Failed("A shortcut is required.");
        }

        if (!entry.TryValidate(out var error))
        {
            return LaunchResult.Failed(error ?? "The shortcut is invalid.");
        }

        try
        {
            Process.Start(CreateStartInfo(entry));
            return LaunchResult.Ok();
        }
        catch (Exception ex)
        {
            // Shell execution reports missing targets, missing associations and permission
            // problems through several exception types; a failed launch must never crash the
            // popup that called this.
            return LaunchResult.Failed(ex.Message);
        }
    }

    private static string GetFileName(string targetPath)
    {
        return IconSource.LooksLikeStoreAppId(targetPath) ? AppsFolderPrefix + targetPath : targetPath;
    }

    private static string GetWorkingDirectory(ShortcutEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? ShortcutEntry.DefaultWorkingDirectoryFor(entry.TargetPath)
            : entry.WorkingDirectory;
    }
}
