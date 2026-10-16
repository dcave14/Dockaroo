namespace Dockaroo.Core;

/// <summary>
/// Resolves where the application keeps its group data (config/, icon-cache/, Shortcuts/) and
/// performs the one-time migration from the legacy layout next to the executable. The default
/// root is <c>%LOCALAPPDATA%\Dockaroo</c>; a <c>portable.txt</c> marker next to the
/// executable keeps the legacy behaviour with the executable's own folder as the root.
/// </summary>
public sealed class DataRootResolver
{
    /// <summary>Marker file next to the executable that selects portable mode.</summary>
    public const string PortableMarkerFileName = "portable.txt";

    /// <summary>Folder name of the default root under LocalAppData.</summary>
    public const string DefaultRootDirectoryName = "Dockaroo";

    /// <summary>Group configuration subfolder name under the root.</summary>
    public const string ConfigDirectoryName = "config";

    /// <summary>Icon cache subfolder name under the root.</summary>
    public const string IconCacheDirectoryName = "icon-cache";

    /// <summary>Taskbar shortcut output subfolder name under the root.</summary>
    public const string ShortcutsDirectoryName = "Shortcuts";

    /// <summary>File name of the appearance preferences inside the data root.</summary>
    public const string AppearanceFileName = "appearance.json";

    private static readonly Lazy<DataRootResolver> CurrentProcess = new(CreateCurrent, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Creates a resolver for an already-resolved root and the executable directory it came from.</summary>
    public DataRootResolver(string rootDirectory, string exeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(exeDirectory);
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        ExeDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDirectory));
    }

    /// <summary>Absolute data root the subfolders are derived from.</summary>
    public string RootDirectory { get; }

    /// <summary>Absolute directory of the running executable (the legacy layout location).</summary>
    public string ExeDirectory { get; }

    /// <summary>Group folders: <c>&lt;root&gt;\config</c>.</summary>
    public string ConfigDirectory => Path.Combine(RootDirectory, ConfigDirectoryName);

    /// <summary>Shared PNG icon cache: <c>&lt;root&gt;\icon-cache</c>.</summary>
    public string IconCacheDirectory => Path.Combine(RootDirectory, IconCacheDirectoryName);

    /// <summary>Default .lnk output folder: <c>&lt;root&gt;\Shortcuts</c>.</summary>
    public string ShortcutsDirectory => Path.Combine(RootDirectory, ShortcutsDirectoryName);

    /// <summary>App-wide appearance preferences (mode + palette), next to the group data.</summary>
    public string AppearanceFile => Path.Combine(RootDirectory, AppearanceFileName);

    /// <summary>
    /// Resolves the data root from the exe directory and a LocalAppData base directory (no
    /// ambient environment access): the exe directory itself in portable mode (<c>portable.txt</c>
    /// next to the executable), otherwise <c>&lt;localAppDataDirectory&gt;\Dockaroo</c>.
    /// </summary>
    public static string Resolve(string exeDirectory, string localAppDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataDirectory);
        var exe = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDirectory));
        if (File.Exists(Path.Combine(exe, PortableMarkerFileName)))
        {
            return exe;
        }

        var baseDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppDataDirectory));
        return Path.Combine(baseDirectory, DefaultRootDirectoryName);
    }

    /// <summary>
    /// Resolver for the running executable: reads the real environment, migrates the legacy
    /// data once and returns the same instance on every later call.
    /// </summary>
    public static DataRootResolver ForCurrentProcess()
    {
        return CurrentProcess.Value;
    }

    /// <summary>
    /// One-time, idempotent migration from the legacy layout in <see cref="ExeDirectory"/>. When
    /// the root has no group folders yet, every group folder under <c>&lt;exe&gt;\config</c> and
    /// every <c>&lt;exe&gt;\Shortcuts\*.lnk</c> is COPIED: nothing is moved or deleted, and an
    /// existing destination file is never overwritten. In portable mode the root is the
    /// executable's folder, so there is nothing to migrate.
    /// </summary>
    public void MigrateLegacyData()
    {
        if (string.Equals(RootDirectory, ExeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            // Portable mode: the legacy layout is already the data root.
            return;
        }

        if (HasGroupFolders(ConfigDirectory))
        {
            // The root already holds groups: the one-time migration ran (or the user has real
            // data here); never copy into it again.
            return;
        }

        var legacyConfig = Path.Combine(ExeDirectory, ConfigDirectoryName);
        if (Directory.Exists(legacyConfig))
        {
            foreach (var groupDirectory in Directory.EnumerateDirectories(legacyConfig))
            {
                CopyTree(groupDirectory, Path.Combine(ConfigDirectory, Path.GetFileName(groupDirectory)));
            }
        }

        var legacyShortcuts = Path.Combine(ExeDirectory, ShortcutsDirectoryName);
        if (Directory.Exists(legacyShortcuts))
        {
            foreach (var shortcut in Directory.EnumerateFiles(legacyShortcuts, "*.lnk"))
            {
                CopyFileIfMissing(shortcut, Path.Combine(ShortcutsDirectory, Path.GetFileName(shortcut)));
            }
        }
    }

    private static DataRootResolver CreateCurrent()
    {
        var exeDirectory = AppContext.BaseDirectory;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var resolver = new DataRootResolver(Resolve(exeDirectory, localAppData), exeDirectory);
        resolver.MigrateLegacyData();
        return resolver;
    }

    /// <summary>A group folder is a config subfolder that holds a group.xml.</summary>
    private static bool HasGroupFolders(string configDirectory)
    {
        return Directory.Exists(configDirectory)
            && Directory.EnumerateDirectories(configDirectory)
                .Any(directory => File.Exists(Path.Combine(directory, GroupStore.ConfigFileName)));
    }

    private static void CopyTree(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            CopyFileIfMissing(sourceFile, Path.Combine(destinationDirectory, relative));
        }
    }

    private static void CopyFileIfMissing(string sourceFile, string destinationFile)
    {
        if (File.Exists(destinationFile))
        {
            // Never overwrite: an existing destination file always wins.
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort migration: one unreadable or locked file must not block startup or
            // the rest of the copy.
        }
    }
}
