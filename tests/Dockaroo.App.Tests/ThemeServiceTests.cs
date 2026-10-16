using System.Drawing;
using Dockaroo.App.Appearance;
using Dockaroo.Core.Appearance;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// App appearance state (task-014). Windows' colour mode is faked through the constructor and
/// no SystemEvents subscription is made; preferences live in a temp folder.
/// </summary>
public sealed class ThemeServiceTests : IDisposable
{
    private readonly string _root;
    private bool _systemLight = true;

    public ThemeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private string FilePath => Path.Combine(_root, "appearance.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a leftover folder is harmless.
        }
    }

    [Fact]
    public void FirstRun_IsSystemMintAndFollowsWindows()
    {
        _systemLight = false;
        using var theme = Create();

        Assert.Equal(AppearancePreferences.Default, theme.Preferences);
        Assert.Null(theme.LoadError);
        Assert.Equal(ResolvedMode.Dark, theme.EffectiveMode);
        Assert.Equal("mint", theme.Palette.Id);
        Assert.Equal(ColorTranslator.FromHtml("#73D2AD"), theme.Tokens.Primary);
    }

    [Fact]
    public void SetPalette_PersistsAndRaisesChangedOnce()
    {
        using var theme = Create();
        var changes = 0;
        theme.Changed += (_, _) => changes++;

        Assert.Null(theme.SetPalette("coral"));
        Assert.Null(theme.SetPalette("coral")); // no-op

        Assert.Equal(1, changes);
        Assert.Equal("coral", theme.Palette.Id);
        using var reopened = Create();
        Assert.Equal(new AppearancePreferences(AppMode.System, "coral"), reopened.Preferences);
    }

    [Fact]
    public void SetMode_ExplicitDarkIgnoresWindowsLight()
    {
        _systemLight = true;
        using var theme = Create();

        Assert.Null(theme.SetMode(AppMode.Dark));

        Assert.Equal(ResolvedMode.Dark, theme.EffectiveMode);
        Assert.Equal(ColorTranslator.FromHtml("#101713"), theme.Tokens.Canvas);
        Assert.Equal(AppMode.Dark, Create().Preferences.Mode);
    }

    [Fact]
    public void UnknownPalette_IsRejectedWithoutChange()
    {
        using var theme = Create();
        var changes = 0;
        theme.Changed += (_, _) => changes++;

        Assert.Contains("'purple' is not a known palette", theme.SetPalette("purple"), StringComparison.Ordinal);
        Assert.Equal("mint", theme.Palette.Id);
        Assert.Equal(0, changes);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void FailedSave_KeepsTheLastSavedSelectionAndReportsIt()
    {
        using var theme = Create();
        Directory.CreateDirectory(FilePath); // the file cannot be written
        var changes = 0;
        theme.Changed += (_, _) => changes++;

        var error = theme.SetPalette("ocean");

        Assert.StartsWith("The appearance settings could not be saved", error, StringComparison.Ordinal);
        Assert.Equal("mint", theme.Palette.Id);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void WindowsModeFlip_ChangesSystemModeOnly()
    {
        _systemLight = true;
        using var theme = Create();
        var changes = 0;
        theme.Changed += (_, _) => changes++;

        _systemLight = false;
        theme.RefreshSystemMode();
        Assert.Equal(ResolvedMode.Dark, theme.EffectiveMode);
        Assert.Equal(1, changes);

        theme.RefreshSystemMode(); // no flip, no event
        Assert.Equal(1, changes);

        Assert.Null(theme.SetMode(AppMode.Light));
        _systemLight = true;
        theme.RefreshSystemMode();
        _systemLight = false;
        theme.RefreshSystemMode();
        Assert.Equal(ResolvedMode.Light, theme.EffectiveMode);
        Assert.Equal(2, changes); // only the SetMode raised; Windows flips are ignored in Light
        Assert.Equal(AppMode.Light, theme.Preferences.Mode); // the saved choice stays explicit
    }

    [Fact]
    public void BadFile_LoadsDefaultsAndExposesTheError()
    {
        File.WriteAllText(FilePath, "{ broken");

        using var theme = Create();

        Assert.Equal(AppearancePreferences.Default, theme.Preferences);
        Assert.StartsWith("The appearance settings are not valid", theme.LoadError, StringComparison.Ordinal);
    }

    [Fact]
    public void Icons_EveryPaletteResourceLoads()
    {
        using var theme = Create();

        foreach (var palette in ThemeCatalog.Default.Palettes)
        {
            using var bitmap = theme.GetIcon(palette.Id, new Size(48, 48)).ToBitmap();
            Assert.Equal(new Size(48, 48), bitmap.Size);
            var opaque = 0;
            for (var y = 0; y < 48; y++)
            {
                for (var x = 0; x < 48; x++)
                {
                    if (bitmap.GetPixel(x, y).A == 255)
                    {
                        opaque++;
                    }
                }
            }

            // Real art, not an empty frame (the wide logo leaves transparent margins: ~15% opaque).
            Assert.True(opaque > 48 * 48 / 10, $"{palette.Id}: only {opaque} opaque pixels");

            // Each palette is its own art: it differs from mint unless it is mint.
            using var mint = theme.GetIcon("mint", new Size(48, 48)).ToBitmap();
            var differing = 0;
            for (var y = 0; y < 48; y++)
            {
                for (var x = 0; x < 48; x++)
                {
                    if (bitmap.GetPixel(x, y) != mint.GetPixel(x, y))
                    {
                        differing++;
                    }
                }
            }

            Assert.Equal(palette.Id == "mint", differing == 0);
        }
    }

    [Fact]
    public void Listening_RequiresAUiMarshal()
    {
        Assert.Throws<ArgumentNullException>(() => new ThemeService(new AppearanceStore(FilePath), systemUsesLight: () => true, listenToSystem: true));
    }

    [Theory]
    [InlineData("Graphite & gold", "Graphite && gold")]
    [InlineData("Mint", "Mint")]
    public void MenuLabel_EscapesMnemonicAmpersands(string name, string label)
    {
        Assert.Equal(label, TrayMessages.MenuLabel(name));
    }

    [Fact]
    public void Icons_ArePerPaletteFramesAtTheRequestedSizeAndCached()
    {
        using var theme = Create();

        var mint = theme.GetIcon("mint", new Size(32, 32));
        Assert.Same(mint, theme.GetIcon("mint", new Size(32, 32)));
        Assert.Equal(new Size(32, 32), mint.Size);
        Assert.Equal(new Size(16, 16), theme.GetIcon("ocean", new Size(16, 16)).Size);

        // Different palettes are different art: compare the rendered 32px frames.
        using var mintBitmap = mint.ToBitmap();
        using var coralBitmap = theme.GetIcon("coral", new Size(32, 32)).ToBitmap();
        var differing = 0;
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                if (mintBitmap.GetPixel(x, y) != coralBitmap.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        Assert.True(differing > 100, $"mint and coral logos differ in only {differing} pixels");
        Assert.Same(theme.GetIcon("mint", new Size(16, 16)), theme.GetAppIcon(new Size(16, 16)));
    }

    [Fact]
    public void MintLogo_IsTheShippedApplicationIcon()
    {
        using var theme = Create();
        var appIconPath = Path.Combine(RepoRoot(), "src", "Dockaroo.App", "Assets", "app-icon.ico");
        using var shipped = new Icon(appIconPath, new Size(32, 32));
        using var shippedBitmap = shipped.ToBitmap();
        using var mintBitmap = theme.GetIcon("mint", new Size(32, 32)).ToBitmap();

        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                Assert.Equal(shippedBitmap.GetPixel(x, y), mintBitmap.GetPixel(x, y));
            }
        }
    }

    private ThemeService Create()
    {
        return new ThemeService(new AppearanceStore(FilePath), systemUsesLight: () => _systemLight, listenToSystem: false);
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
