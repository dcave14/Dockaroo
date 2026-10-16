using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class IconExtractorTests : IDisposable
{
    private const int Size = 64;

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Extract_FromExe_ReturnsBitmapOfRequestedSize()
    {
        var result = IconExtractor.Extract(IconSource.FromPath(NotepadPath()), Size);

        Assert.False(result.IsMissing, result.Error);
        var bitmap = Assert.IsType<Bitmap>(result.Bitmap);
        Assert.Equal(Size, bitmap.Width);
        Assert.Equal(Size, bitmap.Height);
    }

    [Fact]
    public void Extract_FromExe_ScalesDownToSmallerRequestedSize()
    {
        var result = IconExtractor.Extract(IconSource.FromPath(NotepadPath()), 32);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(32, result.Bitmap!.Width);
        Assert.Equal(32, result.Bitmap.Height);
    }

    [Fact]
    public void Extract_FromFolder_ReturnsBitmap()
    {
        var result = IconExtractor.Extract(IconSource.FromPath(_temp.Path), Size);

        Assert.False(result.IsMissing, result.Error);
        var bitmap = Assert.IsType<Bitmap>(result.Bitmap);
        Assert.Equal(Size, bitmap.Width);
        Assert.Equal(Size, bitmap.Height);
    }

    [Fact]
    public void Extract_FromPng_ReturnsSourceDimensions()
    {
        var path = Path.Combine(_temp.Path, "sample.png");
        using (var bitmap = new Bitmap(48, 32))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.CornflowerBlue);
            bitmap.Save(path, ImageFormat.Png);
        }

        var result = IconExtractor.Extract(IconSource.FromPath(path), Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(48, result.Bitmap!.Width);
        Assert.Equal(32, result.Bitmap.Height);
    }

    [Fact]
    public void Extract_FromLnk_UsesResolvedTarget()
    {
        var lnkPath = Path.Combine(_temp.Path, "notepad.lnk");
        CreateTestShortcut(lnkPath, NotepadPath());

        var info = LnkResolver.Resolve(lnkPath);
        Assert.NotNull(info);
        Assert.Equal(NotepadPath(), info!.TargetPath, ignoreCase: true);

        var result = IconExtractor.Extract(IconSource.FromPath(lnkPath), Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(Size, result.Bitmap!.Width);
    }

    [Fact]
    public void Extract_FromLnkWithIconLocation_UsesCustomIcon()
    {
        var icoPath = Path.Combine(_temp.Path, "custom.ico");
        WriteRedTestIcon(icoPath, 32);
        var lnkPath = Path.Combine(_temp.Path, "custom-icon.lnk");
        CreateTestShortcut(lnkPath, NotepadPath(), icoPath);

        var info = LnkResolver.Resolve(lnkPath);
        Assert.NotNull(info);
        Assert.Equal(icoPath, info!.IconLocation, ignoreCase: true);

        var result = IconExtractor.Extract(IconSource.FromPath(lnkPath), Size);

        Assert.False(result.IsMissing, result.Error);
        var center = result.Bitmap!.GetPixel(Size / 2, Size / 2);
        Assert.True(
            center.R > 200 && center.G < 80 && center.B < 80,
            $"Expected the red test icon at the center, got {center}.");
    }

    [Fact]
    public void Extract_FromLnkWithMissingIconLocation_FallsBackToTargetWithoutCrashing()
    {
        var lnkPath = Path.Combine(_temp.Path, "ghost-icon.lnk");
        CreateTestShortcut(lnkPath, NotepadPath(), Path.Combine(_temp.Path, "ghost.ico"));

        var result = IconExtractor.Extract(IconSource.FromPath(lnkPath), Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(Size, result.Bitmap!.Width);
    }

    [Fact]
    public void Extract_NotepadAt48_KeepsTransparentAndAntiAliasedAlphaPixels()
    {
        var result = IconExtractor.Extract(IconSource.FromPath(NotepadPath()), 48);

        Assert.False(result.IsMissing, result.Error);
        AssertAlphaTransparency(result.Bitmap!);
    }

    [Fact]
    public void Extract_FolderAt48_KeepsTransparentAndAntiAliasedAlphaPixels()
    {
        var result = IconExtractor.Extract(IconSource.ForFolder(@"C:\Windows"), 48);

        Assert.False(result.IsMissing, result.Error);
        AssertAlphaTransparency(result.Bitmap!);
    }

    [Fact]
    public void Extract_LnkAt48_KeepsTransparentAndAntiAliasedAlphaPixels()
    {
        var shortcut = GroupLnkWriter.Create("Alpha Test", NotepadPath(), _temp.Path);
        Assert.True(shortcut.Success, shortcut.Error);

        var result = IconExtractor.Extract(IconSource.FromPath(shortcut.LnkPath!), 48);

        Assert.False(result.IsMissing, result.Error);
        AssertAlphaTransparency(result.Bitmap!);
    }

    /// <summary>
    /// P0-1 content assertion: the extracted icon keeps its alpha channel. Pixel (0,0) lies
    /// outside the icon art and must be fully transparent; at least one anti-aliased edge pixel
    /// with a partial alpha proves the channel survives extraction and resizing.
    /// </summary>
    private static void AssertAlphaTransparency(Bitmap bitmap)
    {
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);

        var partialAlpha = false;
        for (var y = 0; y < bitmap.Height && !partialAlpha; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var alpha = bitmap.GetPixel(x, y).A;
                if (alpha is > 0 and < 255)
                {
                    partialAlpha = true;
                    break;
                }
            }
        }

        Assert.True(partialAlpha, "Expected at least one anti-aliased pixel with 0 < alpha < 255.");
    }

    [Fact]
    public void Extract_MissingFile_ReturnsMissingStateWithoutThrowing()
    {
        var missing = Path.Combine(_temp.Path, "does-not-exist.exe");

        var result = IconExtractor.Extract(IconSource.FromPath(missing), Size);

        Assert.True(result.IsMissing);
        Assert.Null(result.Bitmap);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Extract_DeletedFile_ReturnsMissingStateWithoutThrowing()
    {
        var path = Path.Combine(_temp.Path, "deleted.png");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        File.Delete(path);

        var result = IconExtractor.Extract(IconSource.FromPath(path), Size);

        Assert.True(result.IsMissing);
        Assert.Null(result.Bitmap);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Extract_CorruptImage_ReturnsMissingStateWithoutThrowing()
    {
        var path = Path.Combine(_temp.Path, "corrupt.png");
        File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03 });

        var result = IconExtractor.Extract(IconSource.FromPath(path), Size);

        Assert.True(result.IsMissing);
        Assert.Null(result.Bitmap);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Extract_FilePassedAsFolder_FallsBackToFileIcon()
    {
        var result = IconExtractor.Extract(IconSource.ForFolder(NotepadPath()), Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(Size, result.Bitmap!.Width);
        Assert.Equal(Size, result.Bitmap.Height);
    }

    [Fact]
    public void Extract_ExtensionlessFile_UsesGenericFileFallback()
    {
        var path = Path.Combine(_temp.Path, "notepad-copy");
        File.Copy(NotepadPath(), path);
        var source = IconSource.FromPath(path);
        Assert.Equal(IconSourceKind.Unknown, source.Kind);

        var result = IconExtractor.Extract(source, Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(Size, result.Bitmap!.Width);
    }

    [Fact]
    public void Extract_MissingFolder_ReturnsMissingStateWithoutThrowing()
    {
        var missing = Path.Combine(_temp.Path, "no-such-folder");

        var result = IconExtractor.Extract(IconSource.ForFolder(missing), Size);

        Assert.True(result.IsMissing);
        Assert.Null(result.Bitmap);
        Assert.NotNull(result.Error);
    }

    [StoreAppFact]
    public void Extract_FromStoreApp_ReturnsShellIcon()
    {
        var resolvable = StoreAppProbe.Candidates.Where(StoreAppProbe.Exists).ToList();
        Assert.NotEmpty(resolvable);

        var failures = new List<string>();
        foreach (var appUserModelId in resolvable)
        {
            var result = IconExtractor.Extract(IconSource.ForStoreApp(appUserModelId), Size);
            if (!result.IsMissing)
            {
                var bitmap = Assert.IsType<Bitmap>(result.Bitmap);
                Assert.Equal(Size, bitmap.Width);
                Assert.Equal(Size, bitmap.Height);
                return;
            }

            failures.Add($"{appUserModelId} ({result.Error})");
        }

        Assert.Fail("Store apps resolved through the shell but produced no icon: " + string.Join("; ", failures));
    }

    private static string NotepadPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(path), "notepad.exe was expected in the system directory.");
        return path;
    }

    private static void CreateTestShortcut(string lnkPath, string targetPath, string? iconLocation = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(shellType);
        dynamic shell = Activator.CreateInstance(shellType!)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        shortcut.TargetPath = targetPath;
        if (iconLocation is not null)
        {
            shortcut.IconLocation = iconLocation;
        }

        shortcut.Save();
    }

    private static void WriteRedTestIcon(string path, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Red);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var stream = File.Create(path);
            icon.Save(stream);
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// Fact that is skipped when no MS Store app can be resolved through shell:AppsFolder. xunit v2
/// has no runtime test skipping (SkipException is v3-only), so the environment probe runs when
/// this attribute is constructed during test discovery.
/// </summary>
public sealed class StoreAppFactAttribute : FactAttribute
{
    public StoreAppFactAttribute()
    {
        Skip = StoreAppProbe.Candidates.Any(StoreAppProbe.Exists)
            ? null
            : "No MS Store app was resolvable through shell:AppsFolder in this environment.";
    }
}

internal static class StoreAppProbe
{
    /// <summary>Well-known MS Store apps that ship with Windows 10/11.</summary>
    public static readonly string[] Candidates =
    {
        "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App",
        "Microsoft.Paint_8wekyb3d8bbwe!App",
        "Microsoft.WindowsStore_8wekyb3d8bbwe!App",
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
    };

    /// <summary>True when the shell can create an image factory item for the app user model id.</summary>
    public static bool Exists(string appUserModelId)
    {
        try
        {
            var iid = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
            SHCreateItemFromParsingName(@"shell:AppsFolder\" + appUserModelId, IntPtr.Zero, ref iid, out _);
            return true;
        }
        catch (Exception ex) when (ex is COMException
            or InvalidCastException
            or NotSupportedException
            or PlatformNotSupportedException
            or EntryPointNotFoundException
            or DllNotFoundException
            or ArgumentException)
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
}
