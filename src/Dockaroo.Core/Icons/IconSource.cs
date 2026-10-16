using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Dockaroo.Core.Icons;

/// <summary>How an icon source is resolved during extraction.</summary>
public enum IconSourceKind
{
    /// <summary>Executable file (.exe) whose icon is stored inside the file.</summary>
    Exe,

    /// <summary>Resource library (.dll, .ocx, .cpl) holding one or more icons.</summary>
    Dll,

    /// <summary>Image file decoded directly (.png, .jpg, .ico, ...).</summary>
    Image,

    /// <summary>Directory whose shell icon is resolved.</summary>
    Folder,

    /// <summary>Windows shortcut (.lnk) resolved through the shell link COM interfaces.</summary>
    Lnk,

    /// <summary>MS Store app identified by its app user model id; nothing on disk.</summary>
    StoreApp,

    /// <summary>Anything else; extraction falls back to shell and associated-icon behavior.</summary>
    Unknown,
}

/// <summary>
/// Identifies one icon to extract: a path (or an app user model id for store apps) plus the
/// kind of source. Classification and cache key derivation are pure string math (no file
/// system access), so a source can be built and hashed on the popup show path.
/// </summary>
public sealed class IconSource
{
    private const int PublisherHashLength = 13;

    /// <summary>
    /// Disk cache format version, prefixed to every cache key so PNGs written by an older
    /// extraction format are never reused. Bump this constant when the cache format changes;
    /// the value must stay usable in a file name, because the cache uses the key as one.
    /// </summary>
    private const string CacheKeyVersion = "v2_";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp",
    };

    private static readonly HashSet<string> LibraryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cpl", ".dll", ".ocx",
    };

    private IconSource(string path, IconSourceKind kind)
    {
        Path = path;
        Kind = kind;
    }

    /// <summary>Absolute file or directory path; the app user model id for store apps.</summary>
    public string Path { get; }

    /// <summary>Classification that drives which extraction strategy is used.</summary>
    public IconSourceKind Kind { get; }

    /// <summary>
    /// Builds a source from a path. Store app ids are recognized first; every other value is
    /// made absolute and classified from its extension shape. No file system probing happens.
    /// </summary>
    public static IconSource FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        if (LooksLikeStoreAppId(trimmed))
        {
            return new IconSource(trimmed, IconSourceKind.StoreApp);
        }

        var fullPath = MakeAbsolute(trimmed);
        return new IconSource(fullPath, Classify(fullPath));
    }

    /// <summary>Builds a folder source for a caller that already knows the path is a directory.</summary>
    public static IconSource ForFolder(string path)
    {
        return new IconSource(MakeAbsolute(path), IconSourceKind.Folder);
    }

    /// <summary>Builds a store app source from its app user model id (PackageFamilyName!AppId).</summary>
    public static IconSource ForStoreApp(string appUserModelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        return new IconSource(appUserModelId.Trim(), IconSourceKind.StoreApp);
    }

    /// <summary>Classifies a path from its shape alone; never touches the file system.</summary>
    public static IconSourceKind Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return IconSourceKind.Unknown;
        }

        if (LooksLikeStoreAppId(path))
        {
            return IconSourceKind.StoreApp;
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Length == 0)
        {
            // An extension-less value can be a file or a directory. Classification is pure
            // string math (zero IO), so it stays Unknown: extraction probes the path at load
            // time and treats a real directory as a folder there (Folder = Directory.Exists),
            // while an extension-less file gets the generic file fallback.
            return IconSourceKind.Unknown;
        }

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return IconSourceKind.Lnk;
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return IconSourceKind.Exe;
        }

        if (LibraryExtensions.Contains(extension))
        {
            return IconSourceKind.Dll;
        }

        return ImageExtensions.Contains(extension) ? IconSourceKind.Image : IconSourceKind.Unknown;
    }

    /// <summary>
    /// True when the value has the shape of an app user model id ("PackageFamilyName!AppId"):
    /// exactly one '!', no path characters, a package family name that ends in the 13-character
    /// publisher hash after '_', and a plain app id. Relative file names that merely contain
    /// '!' therefore never match.
    /// </summary>
    public static bool LooksLikeStoreAppId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains('\\') || trimmed.Contains('/') || System.IO.Path.IsPathRooted(trimmed))
        {
            return false;
        }

        var separator = trimmed.IndexOf('!');
        if (separator <= 0
            || separator != trimmed.LastIndexOf('!')
            || separator == trimmed.Length - 1)
        {
            return false;
        }

        return IsPackageFamilyName(trimmed[..separator]) && IsAppUserModelIdPart(trimmed[(separator + 1)..]);
    }

    private static bool IsPackageFamilyName(string value)
    {
        // A package family name is "Name_PublisherId", where the publisher id is a 13-character
        // alphanumeric hash; that suffix is what tells an AUMID apart from a file name.
        var underscore = value.LastIndexOf('_');
        return underscore > 0
            && value.Length - underscore - 1 == PublisherHashLength
            && IsAppUserModelIdPart(value);
    }

    private static bool IsAppUserModelIdPart(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Stable, file-name-safe cache key for one requested size: the <see cref="CacheKeyVersion"/>
    /// prefix, then the lowercase hex SHA-256 of kind, normalized path and size. Pure string
    /// math, so looking a key up never does IO and recreating an equivalent source yields the
    /// same key. The version prefix means a cache written by an older extraction format (for
    /// example the pre-fix PNGs with opaque black backgrounds) is never reused. The key
    /// deliberately has no file-write-time component, so when a source at the same path
    /// changes, the cached icon is stale until the caller passes <c>forceReload</c> to the
    /// cache.
    /// </summary>
    public string GetCacheKey(int size)
    {
        var normalized = string.Concat(
            Kind.ToString(),
            "|",
            Path.ToLowerInvariant(),
            "|",
            size.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return CacheKeyVersion + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Diagnostic form: "Kind: Path".</summary>
    public override string ToString()
    {
        return Kind + ": " + Path;
    }

    private static string MakeAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return System.IO.Path.GetFullPath(path.Trim());
    }
}
