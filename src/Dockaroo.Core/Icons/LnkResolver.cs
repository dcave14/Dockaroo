using System.Runtime.InteropServices;
using System.Text;

namespace Dockaroo.Core.Icons;

/// <summary>Fields read from a Windows .lnk shortcut; absent values are null.</summary>
/// <param name="TargetPath">Resolved target file, folder, or app user model id for MS Store shortcuts.</param>
/// <param name="IconLocation">Explicit icon file recorded in the shortcut, if any.</param>
/// <param name="IconIndex">Index of the icon inside <see cref="IconLocation"/>.</param>
/// <param name="AppUserModelId">AppUserModelID property (used by MS Store shortcuts and some desktop apps).</param>
public sealed record LnkInfo(string? TargetPath, string? IconLocation, int IconIndex, string? AppUserModelId);

/// <summary>
/// Reads .lnk shortcuts through the shell link COM interfaces (IShellLinkW, IPersistFile and
/// IPropertyStore) instead of taking a Windows.winmd or WSH dependency. Resolution never
/// throws: a missing or unreadable shortcut returns null.
/// </summary>
public static class LnkResolver
{
    private const int MaxPath = 260;
    private const uint StgmRead = 0;
    private const ushort VtBstr = 8;
    private const ushort VtLpWstr = 31;
    private const int PropVariantUnionOffset = 8;

    /// <summary>
    /// Reads target path, icon location/index and app user model id from a shortcut. Returns
    /// null when the shortcut is missing or cannot be read.
    /// </summary>
    public static LnkInfo? Resolve(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(lnkPath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            // The COM RCWs created here (the shell link and its interfaces) are intentionally
            // left to the GC finalizer rather than Marshal.ReleaseComObject: resolution runs
            // off the popup show path, and delayed release keeps the interop lifetimes simple.
            var link = (IShellLinkW)new CShellLink();
            ((IPersistFile)link).Load(fullPath, StgmRead);

            var targetBuffer = new StringBuilder(MaxPath);
            link.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0);

            var iconBuffer = new StringBuilder(MaxPath);
            link.GetIconLocation(iconBuffer, iconBuffer.Capacity, out var iconIndex);

            return new LnkInfo(
                EmptyToNull(targetBuffer.ToString()),
                EmptyToNull(iconBuffer.ToString()),
                iconIndex,
                ReadAppUserModelId(link));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    private static bool IsRecoverable(Exception ex)
    {
        return ex is COMException
            or InvalidCastException
            or NotSupportedException
            or PlatformNotSupportedException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException;
    }

    private static string? ReadAppUserModelId(IShellLinkW link)
    {
        if (link is not IPropertyStore store)
        {
            return null;
        }

        var key = PropertyKeys.AppUserModelId;
        var buffer = Marshal.AllocHGlobal(PropVariantSize);
        try
        {
            // Zero the PROPVARIANT (VT_EMPTY) before the call so the clear in the finally
            // block is safe even when GetValue fails before writing anything.
            for (var offset = 0; offset < PropVariantSize; offset += IntPtr.Size)
            {
                Marshal.WriteIntPtr(buffer, offset, IntPtr.Zero);
            }

            return store.GetValue(ref key, buffer) < 0 ? null : ReadStringFromPropVariant(buffer);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            NativeMethods.PropVariantClear(buffer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? ReadStringFromPropVariant(IntPtr buffer)
    {
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
        return EmptyToNull(value);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
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
    }
}
