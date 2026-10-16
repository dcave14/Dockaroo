using System.Runtime.InteropServices;
using System.Text;

namespace Dockaroo.Core.Linking;

/// <summary>Result of writing one group shortcut; always returned, never thrown.</summary>
/// <param name="Success">True when the shortcut was written.</param>
/// <param name="LnkPath">Full path of the written shortcut; null on failure.</param>
/// <param name="Error">Failure reason; null on success.</param>
public sealed record GroupLnkResult(bool Success, string? LnkPath, string? Error);

/// <summary>
/// Writes the per-group taskbar shortcut. The .lnk targets the app executable, passes the group
/// name as its argument (the second-instance format documented in BUILD.md), carries the group
/// .ico when one exists (the exe icon otherwise) and sets the AppUserModelID property, so a
/// pinned button for it appears as its own taskbar app instead of stacking with the resident.
/// <para>
/// The COM interop mirrors <see cref="Dockaroo.Core.Icons.LnkResolver"/> (IShellLinkW +
/// IPersistFile + IPropertyStore) on the write side. Unlike the resolver this is a user action,
/// so the RCW is released eagerly and the PROPVARIANT buffer is cleared and freed.
/// </para>
/// </summary>
public static class GroupLnkWriter
{
    private const string LnkExtension = ".lnk";
    private const string MenuSegment = ".menu.";
    private const uint StgmReadWrite = 2;
    private const ushort VtBstr = 8;
    private const ushort VtLpWstr = 31;
    private const int PropVariantUnionOffset = 8;

    /// <summary>
    /// Full AppUserModelID for a group: <c>&lt;base&gt;.menu.&lt;group_name&gt;</c>. Spaces become
    /// underscores because an AppUserModelID must not contain spaces; group names never contain
    /// underscores, so two groups can never share an id.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a valid group name.</exception>
    public static string DeriveAppUserModelId(string groupName)
    {
        if (!Group.IsValidName(groupName))
        {
            throw new ArgumentException($"'{groupName}' is not a valid group name.", nameof(groupName));
        }

        return AppConstants.AppUserModelIdBase + MenuSegment + groupName.Replace(' ', '_');
    }

    /// <summary>
    /// The argument the shortcut passes to the executable: the quoted group name, matching the
    /// documented second-instance command line (<c>Dockaroo.exe "My Group"</c>). Group
    /// names cannot contain quotes, so the quoting is unambiguous.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a valid group name.</exception>
    public static string BuildLaunchArguments(string groupName)
    {
        if (!Group.IsValidName(groupName))
        {
            throw new ArgumentException($"'{groupName}' is not a valid group name.", nameof(groupName));
        }

        return "\"" + groupName + "\"";
    }

    /// <summary>Default output: the user's Desktop, where the .lnk is easy to drag onto the taskbar.</summary>
    public static string DefaultOutputDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <summary>
    /// Writes (or replaces) <c>&lt;outputDirectory&gt;/&lt;group-name&gt;.lnk</c>. Relative
    /// paths are resolved against the current directory and a missing output directory is
    /// created. Overwrite semantics follow the legacy ShellLink save: an existing .lnk at the
    /// same path is replaced in place. The working directory defaults to the executable's
    /// directory; without <paramref name="iconPath"/> the shortcut icon falls back to the exe.
    /// When <paramref name="groupStore"/> is given, the group must load from it: a shortcut for
    /// a group the store cannot serve would open nothing, so nothing is written in that case.
    /// </summary>
    public static GroupLnkResult Create(
        string groupName,
        string exePath,
        string outputDirectory,
        string? iconPath = null,
        string? workingDirectory = null,
        GroupStore? groupStore = null)
    {
        if (!Group.IsValidName(groupName))
        {
            return new GroupLnkResult(
                false,
                null,
                $"'{groupName}' is not a valid group name: letters, digits and spaces only, "
                    + $"at most {Group.MaxNameLength} characters, no leading or trailing spaces.");
        }

        if (groupStore is not null)
        {
            var groupError = CheckGroupExists(groupStore, groupName);
            if (groupError is not null)
            {
                return new GroupLnkResult(false, null, groupError);
            }
        }

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new GroupLnkResult(false, null, "An executable path is required.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return new GroupLnkResult(false, null, "An output directory is required.");
        }

        string lnkPath;
        string fullExePath;
        string fullWorkingDirectory;
        string fullIconPath;
        try
        {
            fullExePath = Path.GetFullPath(exePath.Trim());
            var directory = Path.GetFullPath(outputDirectory.Trim());
            lnkPath = Path.Combine(directory, groupName + LnkExtension);
            fullWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Path.GetDirectoryName(fullExePath) ?? directory
                : Path.GetFullPath(workingDirectory.Trim());
            fullIconPath = string.IsNullOrWhiteSpace(iconPath)
                ? fullExePath
                : Path.GetFullPath(iconPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new GroupLnkResult(false, null, ex.Message);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            WriteShortcut(
                lnkPath,
                fullExePath,
                BuildLaunchArguments(groupName),
                fullWorkingDirectory,
                fullIconPath,
                DeriveAppUserModelId(groupName));
            return new GroupLnkResult(true, lnkPath, null);
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            return new GroupLnkResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Sets the AppUserModelID of the current process (launch-side parity with the legacy
    /// client). The short-lived second instance carries the group id so the pinned taskbar
    /// button shows the running state during the handoff. Returns false when the shell rejects
    /// the id or none was given.
    /// </summary>
    public static bool ApplyCurrentProcessAppUserModelId(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        return NativeMethods.SetCurrentProcessExplicitAppUserModelID(appUserModelId) >= 0;
    }

    /// <summary>
    /// Ensures an existing shortcut carries <paramref name="appUserModelId"/>: when the
    /// property is missing, the shortcut is loaded, the property is set and the same path is
    /// saved in place - the file is never renamed, deleted or recreated and every other
    /// property stays untouched. A shortcut that already carries an AppUserModelID is left
    /// byte for byte alone, so repeated calls are idempotent. Returns false when the shortcut
    /// cannot be read or written; a missing file is false too.
    /// </summary>
    public static bool EnsureAppUserModelId(string lnkPath, string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || string.IsNullOrWhiteSpace(appUserModelId))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(lnkPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        object? shellObject = null;
        try
        {
            shellObject = new CShellLink();
            var link = (IShellLinkW)shellObject;

            // Loading through IPersistFile (STGM_READWRITE: the property store must be writable)
            // binds the property store to the existing file; Save writes the same path back.
            ((IPersistFile)shellObject).Load(fullPath, StgmReadWrite);

            var store = (IPropertyStore)shellObject;
            if (ReadAppUserModelId(store) is not null)
            {
                return true;
            }

            SetAppUserModelId(store, appUserModelId);
            ((IPersistFile)shellObject).Save(fullPath, true);
            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            return false;
        }
        finally
        {
            if (shellObject is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(shellObject);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    // Best effort: the repair result is already decided.
                }
            }
        }
    }

    /// <summary>
    /// Reads the AppUserModelID stored in a shortcut's property store, or null when the
    /// property is absent or the store refuses the read.
    /// </summary>
    private static string? ReadAppUserModelId(IPropertyStore propertyStore)
    {
        var key = PropertyKeys.AppUserModelId;
        var buffer = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            // Zero the PROPVARIANT (VT_EMPTY) first so the clear in the finally block stays
            // safe even when GetValue fails before writing anything.
            for (var offset = 0; offset < PropVariantSize; offset += IntPtr.Size)
            {
                Marshal.WriteIntPtr(buffer, offset, IntPtr.Zero);
            }

            if (propertyStore.GetValue(ref key, buffer) < 0)
            {
                return null;
            }

            var valueType = (ushort)Marshal.ReadInt16(buffer);
            if (valueType != VtLpWstr && valueType != VtBstr)
            {
                return null;
            }

            var pointer = Marshal.ReadIntPtr(buffer, PropVariantUnionOffset);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            var value = valueType == VtLpWstr
                ? Marshal.PtrToStringUni(pointer)
                : Marshal.PtrToStringBSTR(pointer);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        finally
        {
            NativeMethods.PropVariantClear(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Rejects groups the store cannot load before anything is written, so a shortcut is never
    /// created for a group the resident would open nothing for. Returns the reason, or null when
    /// the group loads.
    /// </summary>
    private static string? CheckGroupExists(GroupStore store, string groupName)
    {
        var load = store.Load(groupName);
        if (load.Status == GroupLoadStatus.Loaded)
        {
            return null;
        }

        return load.Status == GroupLoadStatus.NotFound
            ? $"Group not found: no config was found for '{groupName}'."
            : load.Error ?? $"The config for '{groupName}' could not be loaded.";
    }

    private static bool IsWriteFailure(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or COMException
            or InvalidCastException
            or NotSupportedException
            or PlatformNotSupportedException
            or ArgumentException;
    }

    private static void WriteShortcut(
        string lnkPath,
        string exePath,
        string arguments,
        string workingDirectory,
        string iconPath,
        string appUserModelId)
    {
        object? shellObject = null;
        try
        {
            shellObject = new CShellLink();
            var link = (IShellLinkW)shellObject;
            link.SetPath(exePath);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(iconPath, 0);

            SetAppUserModelId((IPropertyStore)shellObject, appUserModelId);

            ((IPersistFile)shellObject).Save(lnkPath, true);
        }
        finally
        {
            if (shellObject is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(shellObject);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
                {
                    // Best effort: the shortcut is already saved, or the write failure is being
                    // reported upward; releasing the RCW must not mask either.
                }
            }
        }
    }

    private static void SetAppUserModelId(IPropertyStore propertyStore, string appUserModelId)
    {
        var key = PropertyKeys.AppUserModelId;
        var buffer = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            // Zero the buffer (VT_EMPTY) first so the clear in the finally block stays safe even
            // when the string allocation or SetValue fails before writing anything.
            for (var offset = 0; offset < PropVariantSize; offset += IntPtr.Size)
            {
                Marshal.WriteIntPtr(buffer, offset, IntPtr.Zero);
            }

            Marshal.WriteInt16(buffer, 0, (short)VtLpWstr);
            Marshal.WriteIntPtr(buffer, PropVariantUnionOffset, Marshal.StringToCoTaskMemUni(appUserModelId));
            propertyStore.SetValue(ref key, buffer);
        }
        finally
        {
            NativeMethods.PropVariantClear(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int PropVariantSize => IntPtr.Size == 8 ? 24 : 16;

    private static class PropertyKeys
    {
        /// <summary>PKEY_AppUserModel_ID: {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5.</summary>
        public static readonly PropertyKey AppUserModelId = new()
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            PropertyId = 5,
        };
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotKey(out short pwHotkey);
        void SetHotKey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassId);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, IntPtr pkey);
        [PreserveSig]
        int GetValue(ref PropertyKey key, IntPtr pv);
        void SetValue(ref PropertyKey key, IntPtr pv);
        void Commit();
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CShellLink
    {
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(IntPtr pvar);

        [DllImport("shell32.dll")]
        internal static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);
    }
}
