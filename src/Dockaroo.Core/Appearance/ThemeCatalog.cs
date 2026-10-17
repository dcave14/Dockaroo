using System.Drawing;
using System.Text.Json;

namespace Dockaroo.Core.Appearance;

/// <summary>
/// Colour tokens for one palette in one mode, parsed from the design handoff's themes.json.
/// Property names follow the token names; <see cref="AccentSoft"/> is the JSON "mint" token (a
/// soft accent in every palette, named after the palette it was first drawn for).
/// </summary>
public sealed class ThemeTokens
{
    internal ThemeTokens(IReadOnlyDictionary<string, Color> colors, IReadOnlyDictionary<string, IReadOnlyList<Color>> gradients)
    {
        Colors = colors;
        Gradients = gradients;
    }

    /// <summary>Every single-colour token by its JSON name.</summary>
    public IReadOnlyDictionary<string, Color> Colors { get; }

    /// <summary>Every multi-stop token (sidebarGradient, desktopGradient, waves) by its JSON name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Color>> Gradients { get; }

    public Color Canvas => Colors["canvas"];

    public Color Surface => Colors["surface"];

    public Color Field => Colors["field"];

    public Color Border => Colors["border"];

    public Color Text => Colors["text"];

    public Color Muted => Colors["muted"];

    public Color Faint => Colors["faint"];

    public Color Primary => Colors["primary"];

    public Color PrimaryHover => Colors["primaryHover"];

    /// <summary>Text on <see cref="Primary"/>; dark in dark themes (never assume white).</summary>
    public Color PrimaryForeground => Colors["primaryForeground"];

    public Color Selected => Colors["selected"];

    public Color SelectedBorder => Colors["selectedBorder"];

    public Color Danger => Colors["danger"];

    public Color Warning => Colors["warning"];

    public Color Sidebar => Colors["sidebar"];

    public Color SidebarSelected => Colors["sidebarSelected"];

    public Color NavText => Colors["navText"];

    public Color NavMuted => Colors["navMuted"];

    public Color NavLine => Colors["navLine"];

    public Color Titlebar => Colors["titlebar"];

    public Color AccentSoft => Colors["mint"];

    public Color Drop => Colors["drop"];

    public Color DropBorder => Colors["dropBorder"];

    public Color Slider => Colors["slider"];

    public Color IconWell => Colors["iconWell"];

    public IReadOnlyList<Color> SidebarGradient => Gradients["sidebarGradient"];

    public IReadOnlyList<Color> DesktopGradient => Gradients["desktopGradient"];

    public IReadOnlyList<Color> Waves => Gradients["waves"];

    /// <summary>Taskbar-preview mockup body fill (tinted chrome, light or dark with the palette).</summary>
    public Color PreviewStrip => Colors["previewStrip"];

    /// <summary>Taskbar-preview mockup 1px edge trim.</summary>
    public Color PreviewTrim => Colors["previewTrim"];

    /// <summary>Taskbar-preview mockup selected button plate.</summary>
    public Color PreviewPlate => Colors["previewPlate"];
}

/// <summary>One palette: display data plus light and dark tokens.</summary>
public sealed record ThemePalette(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<Color> Swatches,
    ThemeTokens Light,
    ThemeTokens Dark)
{
    /// <summary>The tokens for a resolved mode.</summary>
    public ThemeTokens For(ResolvedMode mode) => mode == ResolvedMode.Dark ? Dark : Light;
}

/// <summary>
/// The five palettes from the embedded themes.json. Parsed once and validated strictly: every
/// palette must carry every token in both modes, so a bad handoff edit fails at startup and in
/// tests rather than as a missing colour at paint time.
/// </summary>
public sealed class ThemeCatalog
{
    private const string ResourceName = "Dockaroo.Core.Appearance.themes.json";

    /// <summary>Single-colour tokens every mode must define.</summary>
    public static readonly IReadOnlyList<string> RequiredColorTokens = new[]
    {
        "canvas", "surface", "field", "border", "text", "muted", "faint", "primary", "primaryHover",
        "primaryForeground", "selected", "selectedBorder", "danger", "warning", "sidebar",
        "sidebarSelected", "navText", "navMuted", "navLine", "titlebar", "mint", "drop", "dropBorder",
        "slider", "iconWell", "previewStrip", "previewTrim", "previewPlate",
    };

    /// <summary>Multi-stop tokens every mode must define.</summary>
    public static readonly IReadOnlyList<string> RequiredGradientTokens = new[] { "sidebarGradient", "desktopGradient", "waves" };

    private static readonly Lazy<ThemeCatalog> Embedded = new(LoadEmbedded);

    private readonly Dictionary<string, ThemePalette> _byId;

    private ThemeCatalog(IReadOnlyList<ThemePalette> palettes)
    {
        Palettes = palettes;
        _byId = palettes.ToDictionary(p => p.Id, StringComparer.Ordinal);
    }

    /// <summary>The catalog shipped inside this assembly.</summary>
    public static ThemeCatalog Default => Embedded.Value;

    /// <summary>Palettes in file order.</summary>
    public IReadOnlyList<ThemePalette> Palettes { get; }

    /// <summary>The palette for an ID; unknown IDs get the default (mint) palette.</summary>
    public ThemePalette Get(string? paletteId)
    {
        return paletteId is not null && _byId.TryGetValue(paletteId, out var palette)
            ? palette
            : _byId[AppearancePreferences.DefaultPaletteId];
    }

    /// <summary>Parses a themes.json document; throws <see cref="FormatException"/> on any gap.</summary>
    public static ThemeCatalog Parse(string json)
    {
        try
        {
            return ParseCore(json);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            // Missing properties, wrong JSON kinds or duplicate palette IDs.
            throw new FormatException($"themes.json is malformed: {ex.Message}", ex);
        }
    }

    private static ThemeCatalog ParseCore(string json)
    {
        using var document = JsonDocument.Parse(json);
        var palettes = new List<ThemePalette>();
        foreach (var palette in document.RootElement.GetProperty("palettes").EnumerateArray())
        {
            var id = palette.GetProperty("id").GetString() ?? throw new FormatException("A palette has no id.");
            var modes = palette.GetProperty("modes");
            palettes.Add(new ThemePalette(
                id,
                palette.GetProperty("name").GetString() ?? id,
                palette.TryGetProperty("description", out var description) ? description.GetString() ?? string.Empty : string.Empty,
                palette.GetProperty("swatches").EnumerateArray().Select(s => ParseColor(s.GetString(), id, "swatches")).ToArray(),
                ParseTokens(modes.GetProperty("light"), id + "/light"),
                ParseTokens(modes.GetProperty("dark"), id + "/dark")));
        }

        foreach (var required in AppearancePreferences.PaletteIds)
        {
            if (!palettes.Any(p => p.Id == required))
            {
                throw new FormatException($"themes.json has no '{required}' palette.");
            }
        }

        return new ThemeCatalog(palettes);
    }

    private static ThemeCatalog LoadEmbedded()
    {
        using var stream = typeof(ThemeCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static ThemeTokens ParseTokens(JsonElement mode, string where)
    {
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal);
        var gradients = new Dictionary<string, IReadOnlyList<Color>>(StringComparer.Ordinal);
        foreach (var property in mode.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                gradients[property.Name] = property.Value.EnumerateArray().Select(v => ParseColor(v.GetString(), where, property.Name)).ToArray();
            }
            else
            {
                colors[property.Name] = ParseColor(property.Value.GetString(), where, property.Name);
            }
        }

        foreach (var name in RequiredColorTokens.Where(n => !colors.ContainsKey(n)))
        {
            throw new FormatException($"themes.json {where} is missing the '{name}' token.");
        }

        foreach (var name in RequiredGradientTokens.Where(n => !gradients.ContainsKey(n) || gradients[n].Count == 0))
        {
            throw new FormatException($"themes.json {where} is missing the '{name}' gradient.");
        }

        return new ThemeTokens(colors, gradients);
    }

    private static Color ParseColor(string? hex, string where, string name)
    {
        if (!Group.IsValidColorHex(hex))
        {
            throw new FormatException($"themes.json {where} '{name}' is not #RRGGBB: '{hex}'.");
        }

        return Color.FromArgb(
            Convert.ToInt32(hex!.Substring(1, 2), 16),
            Convert.ToInt32(hex.Substring(3, 2), 16),
            Convert.ToInt32(hex.Substring(5, 2), 16));
    }
}
