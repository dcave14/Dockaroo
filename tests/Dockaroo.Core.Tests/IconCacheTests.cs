using System.Drawing;
using Dockaroo.Core.Icons;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class IconCacheTests : IDisposable
{
    private const int Size = 64;

    private readonly TempDirectory _cacheRoot = new();
    private readonly TempDirectory _temp = new();
    private int _extractCalls;

    public void Dispose()
    {
        _temp.Dispose();
        _cacheRoot.Dispose();
    }

    [Fact]
    public void TryGet_BeforeAnyLoad_ReturnsFalseAndDoesNotExtract()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(NotepadPath());

        Assert.False(cache.TryGet(source, Size, out var result));
        Assert.Null(result);
        Assert.Equal(0, _extractCalls);
    }

    [Fact]
    public void Preload_ThenTryGet_ServesIconWithoutFurtherExtraction()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(NotepadPath());

        Assert.Equal(1, cache.Preload(new[] { source }, Size));
        Assert.Equal(1, _extractCalls);
        Assert.True(Directory.Exists(_cacheRoot.Path));

        // The show path must not touch the disk either: delete the whole disk cache before
        // TryGet so a hit can only come from memory.
        Directory.Delete(_cacheRoot.Path, recursive: true);

        Assert.True(cache.TryGet(source, Size, out var result));
        Assert.False(result!.IsMissing);
        Assert.Equal(Size, result.Bitmap!.Width);
        Assert.Equal(Size, result.Bitmap.Height);
        Assert.Equal(1, _extractCalls);

        var unseen = IconSource.FromPath(Path.Combine(_temp.Path, "unseen.exe"));
        Assert.False(cache.TryGet(unseen, Size, out _));
        Assert.Equal(1, _extractCalls);
    }

    [Fact]
    public void GetOrLoad_SecondCallIsServedFromMemory()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(NotepadPath());

        var first = cache.GetOrLoad(source, Size);
        var second = cache.GetOrLoad(source, Size);

        Assert.False(first.IsMissing, first.Error);
        Assert.Same(first.Bitmap, second.Bitmap);
        Assert.Equal(1, _extractCalls);
    }

    [Fact]
    public void DiskCache_RoundTrip_ReloadsPngWithoutExtractingAgain()
    {
        var source = IconSource.FromPath(NotepadPath());
        var expectedFile = Path.Combine(_cacheRoot.Path, source.GetCacheKey(Size) + ".png");

        var first = new IconCache(_cacheRoot.Path, CountingExtractor);
        var original = first.GetOrLoad(source, Size);

        Assert.False(original.IsMissing, original.Error);
        Assert.Equal(1, _extractCalls);
        Assert.True(File.Exists(expectedFile));

        var second = new IconCache(_cacheRoot.Path, CountingExtractor);
        var reloaded = second.GetOrLoad(source, Size);

        Assert.False(reloaded.IsMissing, reloaded.Error);
        Assert.Equal(1, _extractCalls);
        Assert.Equal(original.Bitmap!.Width, reloaded.Bitmap!.Width);
        Assert.Equal(original.Bitmap.Height, reloaded.Bitmap.Height);
        Assert.NotSame(original.Bitmap, reloaded.Bitmap);
    }

    [Fact]
    public void GetOrLoad_MissingSource_RemembersMissingStateInMemoryOnly()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(Path.Combine(_temp.Path, "missing.exe"));

        var first = cache.GetOrLoad(source, Size);
        var second = cache.GetOrLoad(source, Size);

        Assert.True(first.IsMissing);
        Assert.NotNull(first.Error);
        Assert.True(second.IsMissing);
        Assert.Equal(1, _extractCalls);
        Assert.False(File.Exists(Path.Combine(_cacheRoot.Path, source.GetCacheKey(Size) + ".png")));

        Assert.True(cache.TryGet(source, Size, out var cachedMissing));
        Assert.True(cachedMissing!.IsMissing);
        Assert.Equal(1, _extractCalls);
    }

    [Fact]
    public void GetOrLoad_ForceReload_ExtractsAgain()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(NotepadPath());

        cache.GetOrLoad(source, Size);
        cache.GetOrLoad(source, Size, forceReload: true);

        Assert.Equal(2, _extractCalls);
    }

    [Fact]
    public void GetOrLoad_CorruptDiskCachePng_DropsFileAndReExtracts()
    {
        var source = IconSource.FromPath(NotepadPath());
        var cacheFile = Path.Combine(_cacheRoot.Path, source.GetCacheKey(Size) + ".png");
        Directory.CreateDirectory(_cacheRoot.Path);
        File.WriteAllBytes(cacheFile, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03 });

        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var result = cache.GetOrLoad(source, Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(Size, result.Bitmap!.Width);
        Assert.Equal(1, _extractCalls);

        // The unreadable file was dropped and replaced by a fresh PNG: a second cache loads it
        // without extracting again.
        var second = new IconCache(_cacheRoot.Path, CountingExtractor);
        var reloaded = second.GetOrLoad(source, Size);

        Assert.False(reloaded.IsMissing, reloaded.Error);
        Assert.Equal(1, _extractCalls);
    }

    [Fact]
    public void GetOrLoad_ForceReload_DisposesReplacedBitmap()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(NotepadPath());

        var first = cache.GetOrLoad(source, Size);
        var second = cache.GetOrLoad(source, Size, forceReload: true);

        Assert.False(first.IsMissing, first.Error);
        Assert.False(second.IsMissing, second.Error);
        Assert.NotSame(first.Bitmap, second.Bitmap);
        Assert.Throws<ArgumentException>(() => first.Bitmap!.Width);
    }

    [Fact]
    public void Dispose_DisposesCachedBitmapsAndBlocksFurtherUse()
    {
        var cache = new IconCache(_cacheRoot.Path, CountingExtractor);
        var source = IconSource.FromPath(NotepadPath());
        var result = cache.GetOrLoad(source, Size);

        cache.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cache.TryGet(source, Size, out _));
        Assert.Throws<ArgumentException>(() => result.Bitmap!.Width);
    }

    [Fact]
    public void GetOrLoad_UsesInjectedExtractor()
    {
        var calls = 0;
        using var fixedBitmap = new Bitmap(8, 8);
        var cache = new IconCache(_cacheRoot.Path, (source, size) =>
        {
            calls++;
            return IconExtractionResult.Ok(fixedBitmap);
        });

        var result = cache.GetOrLoad(IconSource.ForFolder(_temp.Path), Size);

        Assert.Same(fixedBitmap, result.Bitmap);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CacheKey_IsStablePerPathAndSensitiveToSize()
    {
        var exe = IconSource.FromPath(NotepadPath());
        var sameExe = IconSource.FromPath(NotepadPath().ToUpperInvariant());

        Assert.Equal(exe.GetCacheKey(Size), sameExe.GetCacheKey(Size));
        Assert.NotEqual(exe.GetCacheKey(Size), exe.GetCacheKey(32));
        Assert.NotEqual(exe.GetCacheKey(Size), IconSource.ForFolder(_temp.Path).GetCacheKey(Size));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe", IconSourceKind.Exe)]
    [InlineData(@"C:\Tools\library.dll", IconSourceKind.Dll)]
    [InlineData(@"C:\Users\me\picture.PNG", IconSourceKind.Image)]
    [InlineData(@"C:\Users\me\shortcut.lnk", IconSourceKind.Lnk)]
    [InlineData(@"C:\Games\notes.txt", IconSourceKind.Unknown)]
    public void FromPath_ClassifiesByExtension(string path, IconSourceKind expected)
    {
        Assert.Equal(expected, IconSource.FromPath(path).Kind);
    }

    [Fact]
    public void FromPath_ExtensionlessPath_IsUnknownNotFolder()
    {
        // Folder means an actual directory (Directory.Exists at extraction time), not the mere
        // absence of an extension: extension-less values may be files, and classification is
        // pure string math that never probes the file system.
        Assert.Equal(IconSourceKind.Unknown, IconSource.FromPath(@"C:\Games\MyFolder").Kind);
        Assert.Equal(IconSourceKind.Unknown, IconSource.FromPath(Path.Combine(_temp.Path, "LICENSE")).Kind);
        Assert.Equal(IconSourceKind.Folder, IconSource.ForFolder(_temp.Path).Kind);
    }

    [Theory]
    [InlineData("important!file.lnk", false)]
    [InlineData("release!notes", false)]
    [InlineData(@"C:\Games\important!file.lnk", false)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("Microsoft.WindowsCalculator!App", false)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App!", false)]
    public void LooksLikeStoreAppId_KeysOnAppUserModelIdShape(string value, bool expected)
    {
        Assert.Equal(expected, IconSource.LooksLikeStoreAppId(value));
    }

    [Fact]
    public void FromPath_RelativeNameWithExclamation_IsNotStoreApp()
    {
        Assert.Equal(IconSourceKind.Lnk, IconSource.FromPath("important!file.lnk").Kind);
    }

    [Fact]
    public void FromPath_ClassifiesAppUserModelIdAsStoreApp()
    {
        var source = IconSource.FromPath("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        Assert.Equal(IconSourceKind.StoreApp, source.Kind);
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", source.Path);
    }

    private IconExtractionResult CountingExtractor(IconSource source, int size)
    {
        _extractCalls++;
        return IconExtractor.Extract(source, size);
    }

    private static string NotepadPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(path), "notepad.exe was expected in the system directory.");
        return path;
    }
}
