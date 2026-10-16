using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Dockaroo.Core.Icons;

/// <summary>
/// Two-tier icon cache: an in-memory table that backs the zero-IO show path and a PNG disk
/// cache (one file per source and size, shared across groups) that backs warm starts.
/// <para>
/// The popup show path uses <see cref="TryGet"/> only; <see cref="GetOrLoad"/> and
/// <see cref="Preload"/> do the IO and are meant for resident startup, saves and change
/// events. The cache directory and the extraction function are injectable, and extraction is
/// only attempted once per source and size while a result (including a missing state) is
/// remembered in memory.
/// </para>
/// <para>
/// Cached bitmaps belong to the cache and must not be disposed by callers. The cache disposes
/// a bitmap as soon as it replaces the entry (a forced reload) and when the cache itself is
/// disposed, so callers must fetch an icon again after a reload instead of painting an old
/// bitmap. All methods are thread-safe; extraction happens outside the lock, so a rare race
/// may extract twice but never blocks lookups.
/// </para>
/// </summary>
public sealed class IconCache : IDisposable
{
    /// <summary>Icon size used when the caller does not name one.</summary>
    public const int DefaultIconSize = 64;

    private const string FileExtension = ".png";
    private const string TempFileSuffix = ".tmp";

    private readonly string _cacheDirectory;
    private readonly Func<IconSource, int, IconExtractionResult> _extractor;
    private readonly object _lock = new();
    private readonly Dictionary<string, IconExtractionResult> _memory = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Creates a cache rooted at <paramref name="cacheDirectory"/>, which is created lazily on
    /// the first disk write. <paramref name="extractor"/> defaults to
    /// <see cref="IconExtractor.Extract"/> and is injectable so callers can observe or replace
    /// extraction.
    /// </summary>
    public IconCache(string cacheDirectory, Func<IconSource, int, IconExtractionResult>? extractor = null)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("A cache directory is required.", nameof(cacheDirectory));
        }

        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _extractor = extractor ?? IconExtractor.Extract;
    }

    /// <summary>Absolute directory holding the PNG disk cache.</summary>
    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// Memory-only lookup for the popup show path. This method must never do IO: it does not
    /// touch the file system, the registry or the shell, and it never triggers extraction; it
    /// only observes results that <see cref="GetOrLoad"/> or <see cref="Preload"/> already
    /// produced. Returns false when the source was never loaded.
    /// </summary>
    public bool TryGet(IconSource source, int size, out IconExtractionResult? result)
    {
        if (source is null)
        {
            result = null;
            return false;
        }

        // Key derivation is pure string math; no IO happens here.
        var key = source.GetCacheKey(size);
        lock (_lock)
        {
            ThrowIfDisposedLocked();
            return _memory.TryGetValue(key, out result);
        }
    }

    /// <summary>
    /// Returns the icon for a source: memory first, then the PNG disk cache, then the
    /// extractor. Must be called outside the popup show path. Successful results are written
    /// to the disk cache; missing states are remembered in memory only, so repeated calls do
    /// not retry extraction unless <paramref name="forceReload"/> is true.
    /// </summary>
    public IconExtractionResult GetOrLoad(IconSource source, int size, bool forceReload = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        var key = source.GetCacheKey(size);

        if (!forceReload)
        {
            lock (_lock)
            {
                ThrowIfDisposedLocked();
                if (_memory.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }

            var fromDisk = TryLoadFromDisk(key);
            if (fromDisk is not null)
            {
                return StoreLoaded(key, fromDisk, replace: false);
            }
        }

        var result = _extractor(source, size);
        if (!result.IsMissing && result.Bitmap is not null)
        {
            TrySaveToDisk(key, result.Bitmap);
        }

        return StoreLoaded(key, result, replace: forceReload);
    }

    /// <summary>
    /// Warms the memory cache for the given sources (memory, then disk, then extraction) and
    /// returns how many produced a bitmap. The resident process calls this at startup before
    /// the first popup show.
    /// </summary>
    public int Preload(IEnumerable<IconSource> sources, int size = DefaultIconSize)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ThrowIfDisposed();
        var loaded = 0;
        foreach (var source in sources)
        {
            if (source is null)
            {
                continue;
            }

            if (!GetOrLoad(source, size).IsMissing)
            {
                loaded++;
            }
        }

        return loaded;
    }

    /// <summary>
    /// Clears the memory cache and disposes every bitmap it holds. The PNG disk cache is left
    /// in place so the next process still starts warm. The cache must not be used afterwards.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _memory.Values)
            {
                entry.Bitmap?.Dispose();
            }

            _memory.Clear();
        }
    }

    /// <summary>
    /// Publishes a loaded result under its key. A replaced entry's bitmap is disposed, because
    /// cached bitmaps are owned by the cache and a forced reload is the explicit handover
    /// point. When a concurrent load already published the same key, the published bitmap wins
    /// instead, so a bitmap another caller may already be painting is never disposed.
    /// </summary>
    private IconExtractionResult StoreLoaded(string key, IconExtractionResult loaded, bool replace)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                loaded.Bitmap?.Dispose();
                throw new ObjectDisposedException(nameof(IconCache));
            }

            if (_memory.TryGetValue(key, out var existing))
            {
                if (!replace)
                {
                    DisposeUnlessSame(loaded.Bitmap, existing.Bitmap);
                    return existing;
                }

                DisposeUnlessSame(existing.Bitmap, loaded.Bitmap);
            }

            _memory[key] = loaded;
            return loaded;
        }
    }

    /// <summary>Disposes <paramref name="candidate"/> unless it is the same instance as <paramref name="kept"/>.</summary>
    private static void DisposeUnlessSame(Bitmap? candidate, Bitmap? kept)
    {
        if (candidate is not null && !ReferenceEquals(candidate, kept))
        {
            candidate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_lock)
        {
            ThrowIfDisposedLocked();
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>.</summary>
    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(IconCache));
        }
    }

    private IconExtractionResult? TryLoadFromDisk(string key)
    {
        var path = GetDiskPath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var loaded = new Bitmap(stream);

            // Copy so the returned bitmap does not keep the file or the stream locked.
            return IconExtractionResult.Ok(new Bitmap(loaded));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ reports unreadable files as ArgumentException/OutOfMemoryException; drop the
            // bad cache file so the next load re-extracts it.
            TryDeleteFile(path);
            return null;
        }
    }

    private void TrySaveToDisk(string key, Bitmap bitmap)
    {
        var path = GetDiskPath(key);
        var tempPath = path + TempFileSuffix;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            bitmap.Save(tempPath, ImageFormat.Png);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            // A failed cache write must never fail the load; the caller still gets the bitmap.
            TryDeleteFile(tempPath);
        }
    }

    private string GetDiskPath(string key)
    {
        return Path.Combine(_cacheDirectory, key + FileExtension);
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
            // Best effort cleanup; a leftover file is overwritten on the next save.
        }
    }
}
