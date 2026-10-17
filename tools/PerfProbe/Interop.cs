#nullable enable
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace PerfProbe;

/// <summary>
/// Minimal IShellLinkW write-side interop so the probe can stamp a show command (SW_HIDE) onto a
/// scratch .lnk (mirrors Dockaroo.Core.Linking.GroupLnkWriter's vtable), plus the read-only
/// taskbar window query the live UIA probe uses. Nothing here launches a shortcut or touches
/// input.
/// </summary>
internal static class Interop
{
    /// <summary>SW_HIDE, the show command for shortcuts that must never show a window.</summary>
    public const int SwHide = 0;

    /// <summary>SW_SHOWNORMAL, the default show command for seeded shortcuts (never launched).</summary>
    public const int SwShowNormal = 1;

    public static void WriteShortcut(string lnkPath, string targetPath, string arguments, string workingDirectory, int showCommand)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        object? shellObject = null;
        try
        {
            shellObject = new CShellLink();
            var link = (IShellLinkW)shellObject;
            link.SetPath(targetPath);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            link.SetShowCmd(showCommand);
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
                    // Best effort: the write result is already decided.
                }
            }
        }
    }

    /// <summary>
    /// Read-only rectangle of the shell's main taskbar window (the exact window
    /// TaskbarLocator.Locate reads), or false when the shell cannot answer. Never moves, clicks
    /// or focuses anything.
    /// </summary>
    public static bool TryTaskbarRect(out Rectangle rect)
    {
        foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var window = FindWindowEx(IntPtr.Zero, IntPtr.Zero, className, null);
            if (window != IntPtr.Zero
                && GetWindowRect(window, out var native)
                && native.Right > native.Left
                && native.Bottom > native.Top)
            {
                rect = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
                return true;
            }
        }

        rect = Rectangle.Empty;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotKey(out short pwHotkey);
        void SetHotKey(short wHotKey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
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
    [Guid("00021401-0000-0000-C000-000000000046")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CShellLink
    {
    }
}
