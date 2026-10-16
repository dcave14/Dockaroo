using System.Runtime.InteropServices;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;

namespace Dockaroo.App;

/// <summary>
/// Per-group pin state, decided on startup and background refreshes and read on the popup show
/// path from memory only. A group counts as pinned when the taskbar's pinned list contains its
/// shortcut or when a pinned-folder copy carries the group's AppUserModelID; a matching pinned
/// copy with a missing AppUserModelID is repaired in place first (content only), so the popup
/// can merge under the pin. The user's pinned folder is never renamed or deleted.
/// </summary>
internal sealed class PinnedStateCache
{
    private readonly string _shortcutsDirectory;
    private readonly string _pinnedDirectory;
    private readonly string _exePath;
    private readonly Func<string, bool?> _pinnedListProbe;
    private readonly object _refreshLock = new();
    private readonly object _stateLock = new();
    private Dictionary<string, string?> _pinned = new(StringComparer.OrdinalIgnoreCase);

    internal PinnedStateCache(
        string shortcutsDirectory,
        string pinnedDirectory,
        string exePath,
        Func<string, bool?>? pinnedListProbe = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinnedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);

        _shortcutsDirectory = shortcutsDirectory;
        _pinnedDirectory = pinnedDirectory;
        _exePath = Path.GetFullPath(exePath);
        _pinnedListProbe = pinnedListProbe ?? PinnedListInterop.IsPinned;
    }

    /// <summary>The current user's taskbar pinned-items folder.</summary>
    internal static string DefaultPinnedDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");
    }

    /// <summary>Memory-only show-path query: is a pin for this group known from the last refresh?</summary>
    internal bool HasPin(string groupName)
    {
        return TryGetPinnedAppUserModelId(groupName, out _);
    }

    /// <summary>
    /// Memory-only show-path query: the AppUserModelID the popup must carry to merge under the
    /// group's pin, or false when no matching pin was found.
    /// </summary>
    internal bool TryGetPinnedAppUserModelId(string groupName, out string? appUserModelId)
    {
        if (!string.IsNullOrEmpty(groupName))
        {
            lock (_stateLock)
            {
                if (_pinned.TryGetValue(groupName, out var pinned) && pinned is not null)
                {
                    appUserModelId = pinned;
                    return true;
                }
            }
        }

        appUserModelId = null;
        return false;
    }

    /// <summary>
    /// Decides each group's pin state and repairs missing AppUserModelIDs in place (the
    /// data-root shortcut and a matching pinned copy). File and shell IO only: the resident
    /// runs this at startup and on background refreshes, never on the popup show path.
    /// </summary>
    internal void Refresh(IReadOnlyList<Group> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        lock (_refreshLock)
        {
            var pinnedFiles = EnumeratePinnedShortcuts();
            var next = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                if (group is null || !Group.IsValidName(group.Name))
                {
                    continue;
                }

                next[group.Name] = FindPin(group.Name, pinnedFiles);
            }

            lock (_stateLock)
            {
                _pinned = next;
            }
        }
    }

    /// <summary>Returns the group's AppUserModelID when a matching pin exists, else null.</summary>
    private string? FindPin(string groupName, IReadOnlyList<string> pinnedFiles)
    {
        var appUserModelId = GroupLnkWriter.DeriveAppUserModelId(groupName);

        // The data-root shortcut is ours by path: fill in a missing AppUserModelID in place.
        var dataRootLnk = Path.Combine(_shortcutsDirectory, groupName + ".lnk");
        var hasDataRootLnk = File.Exists(dataRootLnk);
        if (hasDataRootLnk)
        {
            GroupLnkWriter.EnsureAppUserModelId(dataRootLnk, appUserModelId);
        }

        // Preferred: the shell's pinned list knows the data-root shortcut's identity.
        if (hasDataRootLnk && _pinnedListProbe(dataRootLnk) == true)
        {
            return appUserModelId;
        }

        // Fallback: a pinned-folder copy carries the group's AppUserModelID, or is ours by
        // name and target with the property missing - fill it and merge.
        foreach (var pinnedLnk in pinnedFiles)
        {
            var info = LnkResolver.Resolve(pinnedLnk);
            if (info is null)
            {
                continue;
            }

            if (string.Equals(info.AppUserModelId, appUserModelId, StringComparison.Ordinal))
            {
                return appUserModelId;
            }

            if (info.AppUserModelId is null
                && IsOurPinnedCopy(pinnedLnk, groupName, info.TargetPath)
                && GroupLnkWriter.EnsureAppUserModelId(pinnedLnk, appUserModelId))
            {
                return appUserModelId;
            }
        }

        return null;
    }

    /// <summary>True when the pinned-file name and target identify the group's own copy.</summary>
    private bool IsOurPinnedCopy(string pinnedLnkPath, string groupName, string? targetPath)
    {
        if (!string.Equals(Path.GetFileName(pinnedLnkPath), groupName + ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(targetPath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(targetPath), _exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private IReadOnlyList<string> EnumeratePinnedShortcuts()
    {
        try
        {
            return Directory.Exists(_pinnedDirectory)
                ? Directory.GetFiles(_pinnedDirectory, "*.lnk")
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// IPinnedList3 read-only probe (CLSID_TaskbandPin), the same vtable the task-010 probe
    /// verified: does the taskbar's pinned list contain the shortcut at this path? Null means
    /// the shell could not answer and the caller falls back to the pinned-folder scan. This
    /// never modifies the pinned list.
    /// </summary>
    private static class PinnedListInterop
    {
        internal static bool? IsPinned(string lnkPath)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                IPinnedList3 pinnedList = (IPinnedList3)new TaskbandPin();
                pidl = ILCreateFromPathW(lnkPath);
                if (pidl == IntPtr.Zero)
                {
                    return null;
                }

                var hr = pinnedList.IsPinned(pidl);
                return hr switch
                {
                    0 => true,
                    1 => false,
                    _ => null,
                };
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or PlatformNotSupportedException)
            {
                return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    ILFree(pidl);
                }
            }
        }

        // Pinned-list vtable slots after IUnknown (identical layout to the verified probe):
        // 1 EnumObjects, 2 GetPinnableInfo, 3 IsPinnable, 4 Resolve, 5 LegacyModify,
        // 6 GetChangeCount, 7 IsPinned, 8 GetPinnedItem, 9 GetAppIDForPinnedItem,
        // 10 ItemChangeNotify, 11 UpdateForRemovedItemsAsNecessary, 12 PinShellLink,
        // 13 GetPinnedItemForAppID, 14 Modify. Only slot 7 is called here.
        [ComImport]
        [Guid("0DD79AE2-D156-45D4-9EEB-3B549769E940")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPinnedList3
        {
            [PreserveSig]
            int Dummy1();

            [PreserveSig]
            int Dummy2();

            [PreserveSig]
            int Dummy3();

            [PreserveSig]
            int Dummy4();

            [PreserveSig]
            int Dummy5();

            [PreserveSig]
            int GetChangeCount(out uint count);

            [PreserveSig]
            int IsPinned(IntPtr pidl);

            [PreserveSig]
            int Dummy8();

            [PreserveSig]
            int Dummy9();

            [PreserveSig]
            int Dummy10();

            [PreserveSig]
            int Dummy11();

            [PreserveSig]
            int Dummy12();

            [PreserveSig]
            int Dummy13();

            [PreserveSig]
            int Modify(IntPtr pidlUnpin, IntPtr pidlPin, uint caller);
        }

        [ComImport]
        [Guid("90AA3A4E-1CBA-4233-B8BB-535773D48449")]
        [ClassInterface(ClassInterfaceType.None)]
        private class TaskbandPin
        {
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr ILCreateFromPathW(string pszPath);

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern void ILFree(IntPtr pidl);
    }
}
