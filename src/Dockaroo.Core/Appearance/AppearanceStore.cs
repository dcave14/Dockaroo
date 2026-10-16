using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockaroo.Core.Appearance;

/// <summary>Result of <see cref="AppearanceStore.Load"/>; always returned, never thrown.</summary>
/// <param name="Preferences">The preferences to use (defaults filled in where needed).</param>
/// <param name="UsedDefaults">True when the file was missing, unreadable or held unknown values.</param>
/// <param name="Error">Why defaults were used for a file that exists; null otherwise.</param>
public sealed record AppearanceLoadResult(AppearancePreferences Preferences, bool UsedDefaults, string? Error);

/// <summary>
/// Reads and writes the appearance preferences file:
/// <c>{"version":1,"appearance":{"mode":"system","paletteId":"mint"}}</c>. Unknown or missing
/// values fall back to the defaults per field. Saves are atomic (temp file in the same folder,
/// then a replace), so a crash never leaves a half-written file. Off the popup show path.
/// </summary>
public sealed class AppearanceStore
{
    /// <summary>Schema version written by this build.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Creates a store for <paramref name="filePath"/> (see DataRootResolver.AppearanceFile).</summary>
    public AppearanceStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>Absolute path of the preferences file.</summary>
    public string FilePath { get; }

    /// <summary>Loads the preferences; a missing file is the normal first run (defaults, no error).</summary>
    public AppearanceLoadResult Load()
    {
        string json;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppearanceLoadResult(AppearancePreferences.Default, true, null);
            }

            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new AppearanceLoadResult(AppearancePreferences.Default, true, $"The appearance settings could not be read: {ex.Message}");
        }

        JsonNode? appearance;
        int? version;
        try
        {
            var root = JsonNode.Parse(json);
            appearance = root?["appearance"];
            version = ReadInt(root, "version");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not JSON, or JSON whose root is not an object (an array or a bare value).
            return new AppearanceLoadResult(AppearancePreferences.Default, true, $"The appearance settings are not valid: {ex.Message}");
        }

        var modeText = ReadString(appearance, "mode");
        var paletteText = ReadString(appearance, "paletteId");
        var modeKnown = AppearancePreferences.TryParseMode(modeText, out var mode);
        var paletteKnown = AppearancePreferences.IsKnownPalette(paletteText);
        var preferences = new AppearancePreferences(mode, paletteKnown ? paletteText! : AppearancePreferences.DefaultPaletteId);
        if (version > FormatVersion)
        {
            // Values that parse are still used; the next save writes this build's format.
            return new AppearanceLoadResult(preferences, !(modeKnown && paletteKnown), $"The appearance settings were written by a newer version (format {version}).");
        }

        if (modeKnown && paletteKnown)
        {
            return new AppearanceLoadResult(preferences, false, null);
        }

        return new AppearanceLoadResult(
            preferences,
            true,
            $"Unknown appearance values (mode '{modeText}', palette '{paletteText}') were replaced with the defaults.");
    }

    /// <summary>Writes <paramref name="preferences"/> atomically. Returns the error text, or null on success.</summary>
    public string? Save(AppearancePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!AppearancePreferences.IsKnownPalette(preferences.PaletteId))
        {
            return $"'{preferences.PaletteId}' is not a known palette.";
        }

        var document = new JsonObject
        {
            ["version"] = FormatVersion,
            ["appearance"] = new JsonObject
            {
                ["mode"] = AppearancePreferences.ModeToString(preferences.Mode),
                ["paletteId"] = preferences.PaletteId,
            },
        };

        // One fixed temp name: a crash between write and move leaves at most this one file,
        // which the next save overwrites. Flushed to disk before the move, so a power loss can
        // not leave an empty settings file behind the rename.
        var directory = Path.GetDirectoryName(FilePath)!;
        var tempPath = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(document.ToJsonString(WriteOptions));
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            TryDelete(tempPath);
            return $"The appearance settings could not be saved: {ex.Message}";
        }
    }

    private static bool IsFileFailure(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
    }

    private static int? ReadInt(JsonNode? parent, string name)
    {
        try
        {
            return parent?[name]?.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonNode? parent, string name)
    {
        try
        {
            return parent?[name]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // Present but not a string (a number, an object): treated as unknown.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // A leftover temp file is harmless; the next save overwrites it.
        }
    }
}
