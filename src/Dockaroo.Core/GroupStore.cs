using System.Xml;
using System.Xml.Serialization;

namespace Dockaroo.Core;

/// <summary>Outcome of loading one group config.</summary>
public enum GroupLoadStatus
{
    /// <summary>The group was loaded, from the cache or from disk.</summary>
    Loaded,

    /// <summary>No group config exists for the requested name.</summary>
    NotFound,

    /// <summary>A config exists but could not be read or failed validation.</summary>
    Corrupt,
}

/// <summary>Result of <see cref="GroupStore.Load"/>; always returned, never thrown.</summary>
public sealed record GroupLoadResult(GroupLoadStatus Status, Group? Group, string? Error);

/// <summary>Result of <see cref="GroupStore.Save"/>.</summary>
public sealed record GroupSaveResult(bool Success, string? Error);

/// <summary>
/// Reads and writes group configs laid out as <c>&lt;root&gt;/&lt;GroupName&gt;/group.xml</c>.
/// <para>
/// Loads keep successful results in an in-memory cache, so hot paths (for example showing
/// the popup) can serve groups through <see cref="TryGetCached"/> or a single
/// <see cref="Preload"/> without any further IO. The cache never aliases caller state: reads
/// hand out a deep copy and writes store a private copy, so no caller can mutate cached data.
/// Missing or corrupt configs are reported through <see cref="GroupLoadResult"/> instead of
/// exceptions.
/// </para>
/// <para>
/// The store is not disposable and holds no file handles: all IO is per call.
/// All methods are thread-safe; <see cref="Save"/> and <see cref="Delete"/> additionally
/// serialize per group name.
/// </para>
/// </summary>
public sealed class GroupStore
{
    /// <summary>Config file name inside each group directory.</summary>
    public const string ConfigFileName = "group.xml";

    private const string TempFileSuffix = ".tmp";

    private static readonly XmlSerializer Serializer = new(typeof(Group));
    private static readonly XmlSerializerNamespaces NoNamespaces = CreateNoNamespaces();

    private readonly string _rootDirectory;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, Group> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> _nameLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a store rooted at <paramref name="rootDirectory"/> (the config directory).</summary>
    public GroupStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("A root directory is required.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Absolute directory holding one subdirectory per group.</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>Pure path math: the config directory a group name maps to. No IO.</summary>
    public string GetGroupDirectory(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Path.Combine(_rootDirectory, ToFolderName(name));
    }

    /// <summary>
    /// Returns an independent copy of a cached group without touching disk; the hot path for
    /// the popup. The cache never hands out the instance it stores, so a caller cannot mutate
    /// cached state.
    /// </summary>
    public bool TryGetCached(string name, out Group? group)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(name ?? string.Empty, out var cached))
            {
                group = cached.Clone();
                return true;
            }

            group = null;
            return false;
        }
    }

    /// <summary>
    /// Loads a group: from the cache when present, otherwise from disk. Successful disk
    /// loads are cached. Returns a status instead of throwing for missing/corrupt configs.
    /// </summary>
    public GroupLoadResult Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !IsSafeFolderName(name))
        {
            return new GroupLoadResult(GroupLoadStatus.NotFound, null, "A valid group name is required.");
        }

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(name, out var cached))
            {
                // Copied out: the caller may edit the result without touching cached state.
                return new GroupLoadResult(GroupLoadStatus.Loaded, cached.Clone(), null);
            }
        }

        var result = ReadFromDisk(name);
        if (result.Status == GroupLoadStatus.Loaded && result.Group is not null)
        {
            // The parsed instance is handed to the caller; the cache stores its own deep copy.
            var cacheEntry = result.Group.Clone();
            lock (_cacheLock)
            {
                _cache[name] = cacheEntry;
            }
        }

        return result;
    }

    /// <summary>Loads every config under the root, fills the cache and skips missing/corrupt configs.</summary>
    public IReadOnlyList<Group> LoadAll()
    {
        var loaded = new List<Group>();
        foreach (var name in ListNames())
        {
            var result = Load(name);
            if (result.Status == GroupLoadStatus.Loaded && result.Group is not null)
            {
                loaded.Add(result.Group);
            }
        }

        return loaded;
    }

    /// <summary>Warms the cache for every group on disk; returns the number of groups cached.</summary>
    public int Preload()
    {
        return LoadAll().Count;
    }

    /// <summary>
    /// Display names of groups that have a config file, sorted. Folder names map back from
    /// underscores to spaces. Returns an empty list when the root does not exist yet.
    /// </summary>
    public IReadOnlyList<string> ListNames()
    {
        var names = new List<string>();
        if (!Directory.Exists(_rootDirectory))
        {
            return names;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
            {
                if (File.Exists(Path.Combine(directory, ConfigFileName)))
                {
                    names.Add(FromFolderName(Path.GetFileName(directory)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return names;
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Writes group.xml atomically: serializes to a temp file next to the target, flushes it
    /// to disk, then moves it into place, so a failure never leaves a partial config behind.
    /// Uniqueness is keyed on a saved config file, so a pre-existing empty or foreign folder
    /// does not block the save. Refuses to replace an already saved group unless
    /// <paramref name="overwrite"/> is true. A failed save removes the temp file, removes the
    /// group directory when this save created it, and drops the cached entry; successful
    /// saves update the cache with a private copy of what was written.
    /// <para>
    /// Saves for the same group name are serialized, so two concurrent saves cannot both pass
    /// the uniqueness check for the same name. The caller's instance is never stored or
    /// serialized directly: the written and cached state is a snapshot taken under that lock.
    /// </para>
    /// </summary>
    public GroupSaveResult Save(Group group, bool overwrite = false)
    {
        if (group is null)
        {
            return new GroupSaveResult(false, "A group is required.");
        }

        if (!group.TryValidate(out var error))
        {
            return new GroupSaveResult(false, error);
        }

        var name = group.Name;
        lock (LockForName(name))
        {
            // Snapshot under the name lock so later caller edits cannot leak into the file or
            // the cache, and concurrent same-name saves cannot interleave their checks.
            var snapshot = group.Clone();
            var directory = GetGroupDirectory(name);
            var file = Path.Combine(directory, ConfigFileName);
            if (File.Exists(file) && !overwrite)
            {
                return new GroupSaveResult(false, $"A group named '{name}' already exists.");
            }

            var tempFile = file + TempFileSuffix;
            var directoryExisted = Directory.Exists(directory);
            try
            {
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Serializer.Serialize(stream, snapshot, NoNamespaces);

                    // Push the temp file to disk before the move so a crash cannot leave a
                    // zero-length group.xml behind.
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempFile, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                TryDeleteFile(tempFile);
                if (!directoryExisted)
                {
                    TryDeleteDirectory(directory);
                }

                lock (_cacheLock)
                {
                    _cache.Remove(name);
                }

                return new GroupSaveResult(false, ex.Message);
            }

            lock (_cacheLock)
            {
                _cache[name] = snapshot;
            }

            return new GroupSaveResult(true, null);
        }
    }

    /// <summary>Deletes a group directory (config and any cached assets). False when nothing was deleted.</summary>
    public bool Delete(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !IsSafeFolderName(name))
        {
            return false;
        }

        // Same-name lock: a Delete cannot interleave with the Save that is writing the group.
        lock (LockForName(name))
        {
            lock (_cacheLock)
            {
                _cache.Remove(name);
            }

            var directory = GetGroupDirectory(name);
            if (!Directory.Exists(directory))
            {
                return false;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gate object serializing Save and Delete per group name. The dictionary grows by one
    /// small object per distinct name ever saved or deleted, which is bounded by the number of
    /// groups a user creates.
    /// </summary>
    private object LockForName(string name)
    {
        lock (_cacheLock)
        {
            if (!_nameLocks.TryGetValue(name, out var gate))
            {
                gate = new object();
                _nameLocks[name] = gate;
            }

            return gate;
        }
    }

    private GroupLoadResult ReadFromDisk(string name)
    {
        var file = Path.Combine(GetGroupDirectory(name), ConfigFileName);
        if (!File.Exists(file))
        {
            return new GroupLoadResult(GroupLoadStatus.NotFound, null, $"No group named '{name}' was found.");
        }

        try
        {
            Group? group;
            using (var stream = File.OpenRead(file))
            {
                group = (Group?)Serializer.Deserialize(stream);
            }

            if (group is null)
            {
                return new GroupLoadResult(GroupLoadStatus.Corrupt, null, "The config file contained no group.");
            }

            Normalize(group);
            if (!group.TryValidate(out var error))
            {
                return new GroupLoadResult(GroupLoadStatus.Corrupt, null, error);
            }

            if (!string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return new GroupLoadResult(
                    GroupLoadStatus.Corrupt,
                    null,
                    $"The config stored for '{name}' describes a group named '{group.Name}'.");
            }

            return new GroupLoadResult(GroupLoadStatus.Loaded, group, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException or FormatException)
        {
            return new GroupLoadResult(GroupLoadStatus.Corrupt, null, ex.Message);
        }
    }

    private static void Normalize(Group group)
    {
        // Legacy configs without a color default to the dark popup color.
        group.ColorHex = string.IsNullOrEmpty(group.ColorHex) ? Group.DarkColorHex : group.ColorHex;
        group.Shortcuts ??= new List<ShortcutEntry>();
        foreach (var entry in group.Shortcuts)
        {
            if (entry is null)
            {
                continue;
            }

            entry.TargetPath ??= string.Empty;
            entry.Name ??= string.Empty;
            entry.Arguments ??= string.Empty;
            entry.WorkingDirectory ??= string.Empty;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the temp file is unreachable garbage; do not mask the save error.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // Non-recursive: only removes the directory when it is empty (expected after
                // the temp file was deleted); never removes foreign content.
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: only our own empty directory is removed; do not mask the save error.
        }
    }

    private static bool IsSafeFolderName(string name)
    {
        if (name.Length == 0 || name is "." or ".." || name != name.Trim())
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static string ToFolderName(string name)
    {
        return name.Replace(' ', '_');
    }

    private static string FromFolderName(string folderName)
    {
        return folderName.Replace('_', ' ');
    }

    private static XmlSerializerNamespaces CreateNoNamespaces()
    {
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, string.Empty);
        return namespaces;
    }
}
