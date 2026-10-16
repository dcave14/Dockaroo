using System.Drawing;
using System.Text.Json;
using Dockaroo.Core.Appearance;
using Xunit;

namespace Dockaroo.Core.Tests;

/// <summary>Appearance preferences, their file and the theme token catalog (task-014).</summary>
public sealed class AppearanceTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    private string FilePath => Path.Combine(_temp.Path, "appearance.json");

    // ---------------------------------------------------------------- preferences

    [Theory]
    [InlineData(AppMode.Light, true, ResolvedMode.Light)]
    [InlineData(AppMode.Light, false, ResolvedMode.Light)]
    [InlineData(AppMode.Dark, true, ResolvedMode.Dark)]
    [InlineData(AppMode.System, true, ResolvedMode.Light)]
    [InlineData(AppMode.System, false, ResolvedMode.Dark)]
    public void Resolve_ExplicitModesIgnoreWindows_SystemFollowsIt(AppMode mode, bool systemLight, ResolvedMode expected)
    {
        Assert.Equal(expected, new AppearancePreferences(mode, "mint").Resolve(systemLight));
    }

    [Fact]
    public void Defaults_AreSystemAndMint()
    {
        Assert.Equal(new AppearancePreferences(AppMode.System, "mint"), AppearancePreferences.Default);
        Assert.Equal(new[] { "original", "ocean", "mint", "coral", "graphite-gold" }, AppearancePreferences.PaletteIds);
        Assert.False(AppearancePreferences.IsKnownPalette("Mint"));
        Assert.False(AppearancePreferences.IsKnownPalette(null));
    }

    // ---------------------------------------------------------------- store

    [Fact]
    public void Load_MissingFile_IsDefaultsWithoutAnError()
    {
        var result = new AppearanceStore(FilePath).Load();

        Assert.Equal(AppearancePreferences.Default, result.Preferences);
        Assert.True(result.UsedDefaults);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(AppMode.Light, "coral", "light")]
    [InlineData(AppMode.Dark, "graphite-gold", "dark")]
    [InlineData(AppMode.System, "original", "system")]
    public void Save_WritesTheSchemaAndLoadsBack(AppMode mode, string palette, string modeText)
    {
        var store = new AppearanceStore(FilePath);

        Assert.Null(store.Save(new AppearancePreferences(mode, palette)));

        using (var document = JsonDocument.Parse(File.ReadAllText(FilePath)))
        {
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("version").GetInt32());
            Assert.Equal(modeText, root.GetProperty("appearance").GetProperty("mode").GetString());
            Assert.Equal(palette, root.GetProperty("appearance").GetProperty("paletteId").GetString());
        }

        var loaded = new AppearanceStore(FilePath).Load();
        Assert.Equal(new AppearancePreferences(mode, palette), loaded.Preferences);
        Assert.False(loaded.UsedDefaults);
        Assert.Null(loaded.Error);
    }

    [Fact]
    public void Save_ReplacesTheFileAndLeavesNoTempFiles()
    {
        var store = new AppearanceStore(FilePath);
        Assert.Null(store.Save(new AppearancePreferences(AppMode.Dark, "ocean")));
        Assert.Null(store.Save(new AppearancePreferences(AppMode.Light, "coral")));

        Assert.Equal(new[] { FilePath }, Directory.GetFiles(_temp.Path));
        Assert.Equal(new AppearancePreferences(AppMode.Light, "coral"), store.Load().Preferences);
    }

    [Fact]
    public void Save_UnknownPalette_IsRejectedAndWritesNothing()
    {
        var error = new AppearanceStore(FilePath).Save(new AppearancePreferences(AppMode.Dark, "purple"));

        Assert.Contains("'purple' is not a known palette", error, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_WhenTheTargetCannotBeReplaced_ReturnsAnErrorAndCleansUp()
    {
        Directory.CreateDirectory(FilePath); // a folder where the file should go

        var error = new AppearanceStore(FilePath).Save(new AppearancePreferences(AppMode.Dark, "ocean"));

        Assert.StartsWith("The appearance settings could not be saved", error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void Load_UnknownValues_FallBackPerField()
    {
        File.WriteAllText(FilePath, """{"version":1,"appearance":{"mode":"purple","paletteId":"coral"}}""");

        var result = new AppearanceStore(FilePath).Load();

        Assert.Equal(new AppearancePreferences(AppMode.System, "coral"), result.Preferences);
        Assert.True(result.UsedDefaults);
        Assert.Contains("mode 'purple'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownPaletteAndNonStringMode_FallBackToDefaults()
    {
        File.WriteAllText(FilePath, """{"version":1,"appearance":{"mode":3,"paletteId":"Mint"}}""");

        var result = new AppearanceStore(FilePath).Load();

        Assert.Equal(AppearancePreferences.Default, result.Preferences);
        Assert.Contains("palette 'Mint'", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json", "are not valid")]
    [InlineData("", "are not valid")]
    [InlineData("[1,2,3]", "are not valid")]
    [InlineData("\"just a string\"", "are not valid")]
    [InlineData("{\"appearance\":\"dark\"}", "Unknown appearance values")]
    [InlineData("{\"version\":1}", "Unknown appearance values")]
    public void Load_BadDocuments_GiveDefaultsAndAnError(string content, string expectedError)
    {
        File.WriteAllText(FilePath, content);

        var result = new AppearanceStore(FilePath).Load();

        Assert.Equal(AppearancePreferences.Default, result.Preferences);
        Assert.True(result.UsedDefaults);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_Utf8Bom_IsAccepted()
    {
        File.WriteAllText(FilePath, """{"version":1,"appearance":{"mode":"dark","paletteId":"ocean"}}""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = new AppearanceStore(FilePath).Load();

        Assert.Equal(new AppearancePreferences(AppMode.Dark, "ocean"), result.Preferences);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Load_NewerFormat_KeepsReadableValuesAndSaysSo()
    {
        File.WriteAllText(FilePath, """{"version":2,"appearance":{"mode":"dark","paletteId":"ocean"}}""");

        var result = new AppearanceStore(FilePath).Load();

        Assert.Equal(new AppearancePreferences(AppMode.Dark, "ocean"), result.Preferences);
        Assert.Contains("newer version (format 2)", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_OverwritesALeftoverTempFileFromACrash()
    {
        File.WriteAllText(FilePath + ".tmp", "half-written");

        Assert.Null(new AppearanceStore(FilePath).Save(new AppearancePreferences(AppMode.Light, "mint")));

        Assert.Equal(new[] { FilePath }, Directory.GetFiles(_temp.Path));
        Assert.Equal(AppMode.Light, new AppearanceStore(FilePath).Load().Preferences.Mode);
    }

    [Fact]
    public void Parse_DuplicateOrMalformed_IsAFormatException()
    {
        Assert.Throws<FormatException>(() => ThemeCatalog.Parse("{}"));
        Assert.Throws<FormatException>(() => ThemeCatalog.Parse("not json"));
    }

    // ---------------------------------------------------------------- catalog

    [Fact]
    public void Catalog_HasTheFivePalettesWithTheirHandoffValues()
    {
        var catalog = ThemeCatalog.Default;

        Assert.Equal(AppearancePreferences.PaletteIds, catalog.Palettes.Select(p => p.Id));
        Assert.Equal(new[] { "Original", "Ocean", "Mint", "Coral", "Graphite & gold" }, catalog.Palettes.Select(p => p.Name));

        var mint = catalog.Get("mint");
        Assert.Equal(Hex("#73D2AD"), mint.Dark.Primary);
        Assert.Equal(Hex("#11291E"), mint.Dark.PrimaryForeground);
        Assert.Equal(Hex("#117E76"), mint.Light.Primary);
        Assert.Equal(Hex("#FFFFFF"), mint.Light.PrimaryForeground);
        Assert.Equal(new[] { Hex("#29945D"), Hex("#18B3AD"), Hex("#A1E9C6") }, mint.Swatches);
        Assert.Equal(Hex("#182D3F"), catalog.Get("original").Light.Text);
        Assert.Equal(Hex("#E2C16B"), catalog.Get("graphite-gold").For(ResolvedMode.Dark).Primary);
        Assert.Equal(3, catalog.Get("coral").Dark.Waves.Count);
    }

    [Fact]
    public void Catalog_UnknownPalette_IsMint()
    {
        Assert.Same(ThemeCatalog.Default.Get("mint"), ThemeCatalog.Default.Get("purple"));
        Assert.Same(ThemeCatalog.Default.Get("mint"), ThemeCatalog.Default.Get(null));
    }

    [Fact]
    public void Catalog_EveryPaletteAndMode_HasReadableTextAndButtons()
    {
        foreach (var palette in ThemeCatalog.Default.Palettes)
        {
            foreach (var mode in new[] { ResolvedMode.Light, ResolvedMode.Dark })
            {
                var tokens = palette.For(mode);
                var where = $"{palette.Id}/{mode}";
                Assert.True(Contrast(tokens.Text, tokens.Canvas) >= 7.0, $"{where} body text");
                Assert.True(Contrast(tokens.Text, tokens.Surface) >= 7.0, $"{where} text on cards");
                Assert.True(Contrast(tokens.PrimaryForeground, tokens.Primary) >= 4.5, $"{where} primary button");
                Assert.True(Contrast(tokens.Muted, tokens.Surface) >= 4.5, $"{where} secondary text");
            }
        }
    }

    [Fact]
    public void Parse_MissingToken_FailsNamingIt()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Dockaroo.Core", "Appearance", "themes.json"))
            .Replace("\"primaryHover\": \"#94D8F4\",", string.Empty, StringComparison.Ordinal);

        var error = Assert.Throws<FormatException>(() => ThemeCatalog.Parse(json));

        Assert.Contains("original/dark is missing the 'primaryHover' token", error.Message, StringComparison.Ordinal);
    }

    private static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Dockaroo.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
