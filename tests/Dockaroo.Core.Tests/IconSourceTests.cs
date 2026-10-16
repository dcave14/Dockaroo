using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dockaroo.Core.Icons;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class IconSourceTests : IDisposable
{
    private const int Size = 48;

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void GetCacheKey_StartsWithCacheVersionAndDiffersFromTheUnversionedKey()
    {
        var source = IconSource.FromPath(Path.Combine(_temp.Path, "app.exe"));

        var key = source.GetCacheKey(Size);
        var unversioned = UnversionedKey(source, Size);

        Assert.StartsWith("v2_", key);
        Assert.NotEqual(unversioned, key);
        Assert.Equal("v2_" + unversioned, key);
    }

    [Fact]
    public void GetCacheKey_VersionIsStableAndSensitiveToSizeAndSource()
    {
        var source = IconSource.FromPath(Path.Combine(_temp.Path, "app.exe"));
        var sameSource = IconSource.FromPath(Path.Combine(_temp.Path, "APP.EXE"));

        Assert.Equal(source.GetCacheKey(Size), sameSource.GetCacheKey(Size));
        Assert.NotEqual(source.GetCacheKey(Size), source.GetCacheKey(32));
        Assert.NotEqual(source.GetCacheKey(Size), IconSource.ForFolder(_temp.Path).GetCacheKey(Size));
    }

    [Fact]
    public void OldFormatPng_AtTheUnversionedKeyPath_IsNotReused()
    {
        var cacheRoot = Path.Combine(_temp.Path, "cache");
        Directory.CreateDirectory(cacheRoot);
        var source = IconSource.FromPath(Path.Combine(_temp.Path, "app.exe"));

        // A valid PNG at the pre-fix (unversioned) file name, like the black-background icons
        // the alpha fix retired: it must never satisfy a load again.
        var oldPath = Path.Combine(cacheRoot, UnversionedKey(source, Size) + ".png");
        using (var oldIcon = new Bitmap(8, 8))
        {
            using var graphics = Graphics.FromImage(oldIcon);
            graphics.Clear(Color.Green);
            oldIcon.Save(oldPath, ImageFormat.Png);
        }

        using var replacement = new Bitmap(8, 8);
        using (var graphics = Graphics.FromImage(replacement))
        {
            graphics.Clear(Color.Red);
        }

        var extractions = 0;
        var cache = new IconCache(cacheRoot, (_, _) =>
        {
            extractions++;
            return IconExtractionResult.Ok(new Bitmap(replacement));
        });

        var result = cache.GetOrLoad(source, Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.Equal(1, extractions);
        Assert.Equal(Color.Red.ToArgb(), result.Bitmap!.GetPixel(0, 0).ToArgb());
        Assert.True(File.Exists(oldPath), "The old cache file must be left on disk untouched.");
    }

    [Fact]
    public void NewPng_IsWrittenUnderTheVersionedKey()
    {
        var cacheRoot = Path.Combine(_temp.Path, "cache");
        var source = IconSource.FromPath(Path.Combine(_temp.Path, "app.exe"));
        using var extracted = new Bitmap(8, 8);
        var cache = new IconCache(cacheRoot, (_, _) => IconExtractionResult.Ok(new Bitmap(extracted)));

        var result = cache.GetOrLoad(source, Size);

        Assert.False(result.IsMissing, result.Error);
        Assert.True(File.Exists(Path.Combine(cacheRoot, source.GetCacheKey(Size) + ".png")));
    }

    /// <summary>The pre-fix key: lowercase hex SHA-256 of kind, normalized path and size.</summary>
    private static string UnversionedKey(IconSource source, int size)
    {
        var normalized = string.Concat(
            source.Kind.ToString(),
            "|",
            source.Path.ToLowerInvariant(),
            "|",
            size.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
