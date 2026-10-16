using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Dockaroo.App;

/// <summary>
/// Hand-offs to the Windows shell for the human pin gesture (Windows 11 allows no programmatic
/// pinning): the drag payload for a .lnk, built the way File Explorer builds it, and opening
/// File Explorer at a shortcut or folder so it can be dragged from there.
/// </summary>
internal static class ShellHandoff
{
    // BHID_DataObject: asks a shell item for its IDataObject.
    private static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");

    /// <summary>Test hook: receives the Explorer arguments instead of starting Explorer.</summary>
    internal static Action<string>? ExplorerLaunchOverride { get; set; }

    /// <summary>
    /// The plain payload: exactly the shortcut path as CF_HDROP. The taskbar's pin target
    /// refuses this on its own, so it is only the fallback of <see cref="CreateShellDragData"/>.
    /// </summary>
    internal static DataObject CreateDragData(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, new[] { shortcutPath });
        return data;
    }

    /// <summary>
    /// The payload a pin drag carries: the shell's own data object for the .lnk, the same one
    /// File Explorer hands out (Shell IDList Array plus CF_HDROP and friends). Falls back to
    /// <see cref="CreateDragData"/> when the shell cannot build one (for example a missing file).
    /// </summary>
    internal static object CreateShellDragData(string shortcutPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        try
        {
            var itemId = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(shortcutPath, IntPtr.Zero, ref itemId, out var item);
            var handler = BhidDataObject;
            var dataObjectId = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
            if (item.BindToHandler(IntPtr.Zero, ref handler, ref dataObjectId, out var shellData) >= 0
                && shellData is System.Runtime.InteropServices.ComTypes.IDataObject comData)
            {
                return comData;
            }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException or InvalidCastException)
        {
            // Fall through to the plain file drop.
        }

        return CreateDragData(shortcutPath);
    }

    /// <summary>Explorer arguments that open the containing folder with the file selected.</summary>
    internal static string ExplorerSelectArguments(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return "/select,\"" + Path.GetFullPath(path) + "\"";
    }

    /// <summary>Explorer arguments that open a folder.</summary>
    internal static string ExplorerFolderArguments(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return "\"" + Path.GetFullPath(folder) + "\"";
    }

    /// <summary>Starts File Explorer with <paramref name="arguments"/>; never throws.</summary>
    internal static void OpenInExplorer(string arguments)
    {
        if (ExplorerLaunchOverride is { } capture)
        {
            capture(arguments);
            return;
        }

        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer could not start; the drag still works.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    // Only BindToHandler (first slot after IUnknown) is called.
    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(
            IntPtr bindContext,
            ref Guid handler,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object result);
    }
}
