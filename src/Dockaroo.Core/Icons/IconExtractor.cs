using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Dockaroo.Core.Icons;

/// <summary>
/// Outcome of one icon extraction attempt: either a bitmap or a missing state. Extraction
/// never throws, so callers always get an object to act on; <see cref="Error"/> carries the
/// reason for a missing state.
/// </summary>
public sealed class IconExtractionResult
{
    private IconExtractionResult(Bitmap? bitmap, string? error)
    {
        Bitmap = bitmap;
        Error = error;
    }

    /// <summary>The extracted bitmap, or null when no icon is available.</summary>
    public Bitmap? Bitmap { get; }

    /// <summary>True when no bitmap could be produced (missing source or failed extraction).</summary>
    public bool IsMissing => Bitmap is null;

    /// <summary>Reason for a missing state; null on success.</summary>
    public string? Error { get; }

    /// <summary>Creates a successful result owning <paramref name="bitmap"/>.</summary>
    public static IconExtractionResult Ok(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return new IconExtractionResult(bitmap, null);
    }

    /// <summary>Creates a missing state with a human-readable reason.</summary>
    public static IconExtractionResult Missing(string error)
    {
        return new IconExtractionResult(null, error);
    }
}

/// <summary>
/// Win32/shell icon extraction per source kind, following the legacy fallback order: a .lnk
/// is resolved through <see cref="LnkResolver"/> (icon location first, then target, then the
/// store app lookup), folders and files go through the modern shell image factory first and
/// fall back to ExtractIconEx / SHGetFileInfo / associated-icon extraction. The requested size
/// is clamped to 16..256; image sources keep their own dimensions. Extraction never throws:
/// any shell or GDI+ failure comes back as a missing state.
/// </summary>
public static class IconExtractor
{
    /// <summary>Smallest requested icon size; anything smaller is clamped up.</summary>
    public const int MinIconSize = 16;

    /// <summary>Largest icon size Windows serves; larger requests are clamped and scaled.</summary>
    public const int MaxIconSize = 256;

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint FileAttributeDirectory = 0x00000010;

    /// <summary>
    /// Extracts a square icon for the given source. Never throws; failures return a missing
    /// state. Image sources are decoded at their own dimensions and ignore the size clamp.
    /// </summary>
    public static IconExtractionResult Extract(IconSource source, int size)
    {
        if (source is null)
        {
            return IconExtractionResult.Missing("No icon source was provided.");
        }

        var targetSize = Math.Clamp(size, MinIconSize, MaxIconSize);
        try
        {
            return source.Kind switch
            {
                IconSourceKind.Image => ExtractImage(source.Path, targetSize),
                IconSourceKind.Folder => ExtractFolder(source.Path, targetSize),
                IconSourceKind.Lnk => ExtractLnk(source.Path, targetSize),
                IconSourceKind.StoreApp => ExtractStoreApp(source.Path, targetSize),
                _ => ExtractGeneric(source.Path, targetSize),
            };
        }
        catch (Exception ex)
        {
            // Shell items and GDI+ can fail in many ways; extraction must never crash a caller
            // that is merely painting a popup row.
            return IconExtractionResult.Missing($"Icon extraction for '{source.Path}' failed ({ex.Message}).");
        }
    }

    private static IconExtractionResult ExtractImage(string path, int size)
    {
        if (!File.Exists(path))
        {
            return IconExtractionResult.Missing($"The image '{path}' was not found.");
        }

        if (IsIconExtension(path))
        {
            return ExtractIconFile(path, size);
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var image = Image.FromStream(stream);
            // Copy before the stream closes and keep the source dimensions.
            return IconExtractionResult.Ok(new Bitmap(image));
        }
        catch (Exception ex) when (IsShellFailure(ex) || ex is OutOfMemoryException)
        {
            return IconExtractionResult.Missing($"The image '{path}' could not be decoded ({ex.Message}).");
        }
    }

    private static IconExtractionResult ExtractFolder(string path, int size)
    {
        if (!Directory.Exists(path))
        {
            // Callers (including .lnk target resolution) can assume a folder when the path is
            // actually a file. Fall through to the generic file fallback in that case instead
            // of reporting a missing folder.
            return File.Exists(path)
                ? ExtractGeneric(path, size)
                : IconExtractionResult.Missing($"The folder '{path}' was not found.");
        }

        var shellImage = TryShellImage(path, size, out _);
        if (shellImage is not null)
        {
            return IconExtractionResult.Ok(shellImage);
        }

        var folderIcon = TryFolderIcon(path, size, out var error);
        if (folderIcon is not null)
        {
            return IconExtractionResult.Ok(folderIcon);
        }

        return IconExtractionResult.Missing(error ?? $"No icon could be extracted from the folder '{path}'.");
    }

    private static IconExtractionResult ExtractLnk(string path, int size)
    {
        if (!File.Exists(path))
        {
            return IconExtractionResult.Missing($"The shortcut '{path}' was not found.");
        }

        var info = LnkResolver.Resolve(path);
        if (info is not null)
        {
            // Legacy order: an explicit icon location wins, then the target, then the
            // store app lookup for shortcuts whose "target" is only an app user model id.
            var iconFile = ExpandEnvironment(info.IconLocation);
            if (!string.IsNullOrEmpty(iconFile)
                && !iconFile.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && File.Exists(iconFile))
            {
                var fromIcon = ExtractModule(iconFile, Math.Max(info.IconIndex, 0), size);
                if (!fromIcon.IsMissing)
                {
                    return fromIcon;
                }
            }

            var target = ExpandEnvironment(info.TargetPath);
            if (!string.IsNullOrEmpty(target))
            {
                if (Directory.Exists(target))
                {
                    var fromFolder = ExtractFolder(target, size);
                    if (!fromFolder.IsMissing)
                    {
                        return fromFolder;
                    }
                }
                else if (File.Exists(target))
                {
                    var fromTarget = ExtractModule(target, 0, size);
                    if (!fromTarget.IsMissing)
                    {
                        return fromTarget;
                    }
                }
                else if (IconSource.LooksLikeStoreAppId(target))
                {
                    var fromStore = ExtractStoreApp(target, size);
                    if (!fromStore.IsMissing)
                    {
                        return fromStore;
                    }
                }
            }

            if (!string.IsNullOrEmpty(info.AppUserModelId))
            {
                var fromProperty = ExtractStoreApp(info.AppUserModelId, size);
                if (!fromProperty.IsMissing)
                {
                    return fromProperty;
                }
            }
        }

        // Last resort: let the shell resolve the shortcut itself.
        var shellImage = TryShellImage(path, size, out _);
        if (shellImage is not null)
        {
            return IconExtractionResult.Ok(shellImage);
        }

        var associated = TryAssociatedIconBitmap(path, size);
        if (associated is not null)
        {
            return IconExtractionResult.Ok(associated);
        }

        return IconExtractionResult.Missing($"No icon could be extracted from the shortcut '{path}'.");
    }

    private static IconExtractionResult ExtractStoreApp(string appUserModelId, int size)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
        {
            return IconExtractionResult.Missing("No app user model id was provided.");
        }

        var aumid = appUserModelId.Trim();
        var bitmap = TryShellImage(@"shell:AppsFolder\" + aumid, size, out var error);
        if (bitmap is not null)
        {
            return IconExtractionResult.Ok(bitmap);
        }

        return IconExtractionResult.Missing(error ?? $"No icon is available for the store app '{aumid}'.");
    }

    private static IconExtractionResult ExtractGeneric(string path, int size)
    {
        if (!File.Exists(path))
        {
            return Directory.Exists(path)
                ? ExtractFolder(path, size)
                : IconExtractionResult.Missing($"The file '{path}' was not found.");
        }

        return ExtractModule(path, 0, size);
    }

    private static IconExtractionResult ExtractModule(string path, int iconIndex, int size)
    {
        if (Directory.Exists(path))
        {
            return ExtractFolder(path, size);
        }

        if (!File.Exists(path))
        {
            return IconExtractionResult.Missing($"The file '{path}' was not found.");
        }

        if (IsIconExtension(path))
        {
            return ExtractIconFile(path, size);
        }

        // An explicit index can only be honored through ExtractIconEx, the shell factory
        // always returns the module's default icon.
        if (iconIndex > 0)
        {
            var indexed = TryExtractIconExBitmap(path, iconIndex, size);
            if (indexed is not null)
            {
                return IconExtractionResult.Ok(indexed);
            }
        }

        var shellImage = TryShellImage(path, size, out _);
        if (shellImage is not null)
        {
            return IconExtractionResult.Ok(shellImage);
        }

        var extracted = TryExtractIconExBitmap(path, 0, size);
        if (extracted is not null)
        {
            return IconExtractionResult.Ok(extracted);
        }

        var associated = TryAssociatedIconBitmap(path, size);
        if (associated is not null)
        {
            return IconExtractionResult.Ok(associated);
        }

        return IconExtractionResult.Missing($"No icon could be extracted from '{path}'.");
    }

    private static IconExtractionResult ExtractIconFile(string path, int size)
    {
        try
        {
            using var icon = new Icon(path, size, size);
            using var bitmap = icon.ToBitmap();
            return IconExtractionResult.Ok(ResizeTo(bitmap, size));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException or OutOfMemoryException)
        {
            return IconExtractionResult.Missing($"The icon file '{path}' could not be read ({ex.Message}).");
        }
    }

    private static Bitmap? TryShellImage(string parsingName, int size, out string? error)
    {
        error = null;
        IShellItemImageFactory? factory = null;
        try
        {
            // The shell item and its image factory are COM RCWs; like the .lnk interop
            // objects they are released by the GC finalizer. Extraction is off the show path,
            // so delayed release is fine and avoids manual shell object lifetime handling.
            var iid = typeof(IShellItemImageFactory).GUID;
            NativeMethods.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item);
            factory = item as IShellItemImageFactory;
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            error = $"The shell could not resolve '{parsingName}' (0x{ex.HResult:X8}).";
            return null;
        }

        if (factory is null)
        {
            error = $"The shell did not return an image factory for '{parsingName}'.";
            return null;
        }

        var bitmapHandle = IntPtr.Zero;
        try
        {
            factory.GetImage(new SIZE(size, size), SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out bitmapHandle);
            if (bitmapHandle == IntPtr.Zero)
            {
                error = $"The shell returned an empty image for '{parsingName}'.";
                return null;
            }

            // GetImage returns a 32-bpp DIB whose alpha channel carries the icon's
            // transparency; Image.FromHbitmap drops that channel and paints every transparent
            // pixel opaque black, so read the DIB rows directly.
            using var readable = TryReadDibBitmap(bitmapHandle) ?? Image.FromHbitmap(bitmapHandle);
            return ResizeTo(readable, size);
        }
        catch (Exception ex) when (IsShellFailure(ex))
        {
            error = $"The shell did not return an image for '{parsingName}' (0x{ex.HResult:X8}).";
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmapHandle);
            }
        }
    }

    /// <summary>
    /// Reads an HBITMAP as a 32-bpp ARGB bitmap with its alpha channel intact. Returns null
    /// when the handle cannot be read as a 32-bpp DIB or when every alpha byte is 0: both mean
    /// the image has no alpha channel at all (a real 24-bpp image), where the legacy
    /// <see cref="Image.FromHbitmap"/> conversion loses nothing. The handle is not released
    /// here; <see cref="TryShellImage"/> owns it.
    /// </summary>
    private static Bitmap? TryReadDibBitmap(IntPtr bitmapHandle)
    {
        if (NativeMethods.GetObject(bitmapHandle, Marshal.SizeOf<BITMAP>(), out var dib) == 0
            || dib.bmWidth <= 0
            || dib.bmHeight == 0
            || dib.bmBits == IntPtr.Zero)
        {
            return null;
        }

        var width = dib.bmWidth;
        var height = Math.Abs(dib.bmHeight);
        var stride = width * 4;
        if (dib.bmBitsPixel != 32 || dib.bmWidthBytes < stride)
        {
            // Not a 32-bpp ARGB DIB: there is no alpha channel that could be preserved.
            return null;
        }

        // A negative height means top-down rows; a positive height means the DIB is stored
        // bottom-up, so the rows are flipped while they are copied.
        var bottomUp = dib.bmHeight > 0;
        var pixels = new byte[stride * height];
        var hasAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var sourceY = bottomUp ? height - 1 - y : y;
            Marshal.Copy(IntPtr.Add(dib.bmBits, sourceY * dib.bmWidthBytes), pixels, y * stride, stride);
            for (var alpha = (y * stride) + 3; alpha < (y + 1) * stride; alpha += 4)
            {
                if (pixels[alpha] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }
        }

        if (!hasAlpha)
        {
            // Every alpha byte is 0: the DIB is really a 24-bpp image stored in a 32-bpp
            // container, so the legacy conversion is the correct fallback.
            return null;
        }

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }

        return bitmap;
    }

    private static Bitmap? TryFolderIcon(string path, int size, out string? error)
    {
        error = null;
        var result = NativeMethods.SHGetFileInfo(
            path,
            FileAttributeDirectory,
            out var info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            error = $"The shell did not return a folder icon for '{path}'.";
            return null;
        }

        var bitmap = TryBitmapFromIconHandle(info.hIcon);
        if (bitmap is null)
        {
            error = $"The folder icon for '{path}' could not be converted to a bitmap.";
            return null;
        }

        using (bitmap)
        {
            return ResizeTo(bitmap, size);
        }
    }

    private static Bitmap? TryExtractIconExBitmap(string path, int iconIndex, int size)
    {
        var handles = new IntPtr[1];
        uint count;
        try
        {
            count = NativeMethods.ExtractIconEx(path, iconIndex, handles, null, 1);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }

        if (count == 0 || handles[0] == IntPtr.Zero)
        {
            return null;
        }

        using var bitmap = TryBitmapFromIconHandle(handles[0]);
        return bitmap is null ? null : ResizeTo(bitmap, size);
    }

    private static Bitmap? TryAssociatedIconBitmap(string path, int size)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = icon.ToBitmap();
            return ResizeTo(bitmap, size);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or IOException or ExternalException or OutOfMemoryException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Takes ownership of <paramref name="iconHandle"/>: the handle is always destroyed.</summary>
    private static Bitmap? TryBitmapFromIconHandle(IntPtr iconHandle)
    {
        if (iconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return icon.ToBitmap();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }

    /// <summary>High quality scale to an exact square size, ported from the legacy ImageFunctions.ResizeImage.</summary>
    private static Bitmap ResizeTo(Image image, int size)
    {
        var destination = new Bitmap(size, size);
        if (image.HorizontalResolution > 0 && image.VerticalResolution > 0)
        {
            destination.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        }

        using var graphics = Graphics.FromImage(destination);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(
            image,
            new Rectangle(0, 0, size, size),
            0,
            0,
            image.Width,
            image.Height,
            GraphicsUnit.Pixel,
            attributes);
        return destination;
    }

    private static string? ExpandEnvironment(string? value)
    {
        return string.IsNullOrEmpty(value) ? value : Environment.ExpandEnvironmentVariables(value);
    }

    private static bool IsIconExtension(string path)
    {
        return Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellFailure(Exception ex)
    {
        return ex is COMException
            or ExternalException
            or InvalidCastException
            or NotSupportedException
            or PlatformNotSupportedException
            or ArgumentException
            or IOException;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int cx, int cy)
        {
            this.cx = cx;
            this.cy = cy;
        }
    }

    /// <summary>The GDI BITMAP header <c>GetObject</c> fills for an HBITMAP created from a DIB.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        IconBackground = 0x10,
        ScaleUp = 0x20,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        internal static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            out SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint ExtractIconEx(
            string lpszFile,
            int nIconIndex,
            IntPtr[]? phiconLarge,
            IntPtr[]? phiconSmall,
            uint nIcons);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        internal static extern int GetObject(IntPtr hObject, int nCount, out BITMAP lpObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr hObject);
    }
}
