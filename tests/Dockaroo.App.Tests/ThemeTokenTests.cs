using System.Drawing;
using Dockaroo.Core.Appearance;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Palette/logo coherence (palette-app-theme) and light-mode richness (light-theme-rework):
/// every palette's accent tokens carry the hue of its logo in both modes, every light mode keeps
/// its structural lines visible at 100% display brightness, every light surface is tinted with
/// its palette (never plain white) and the miniature preview wallpaper keeps each palette's hue
/// family across modes instead of bleaching in light. The blue dark tokens that original carried
/// used to fail the hue check; the washed-out light borders used to fail the contrast floor; the
/// white light surfaces and the khaki-vs-plum wallpaper drift used to fail the tint checks.
/// </summary>
public sealed class ThemeTokenTests
{
    /// <summary>Minimum relative-luminance contrast for light-mode structural hairlines.</summary>
    private const double MinLineContrast = 2.5;

    /// <summary>Minimum HSV saturation for a light surface (canvas, sidebar, card, field).</summary>
    private const double MinSurfaceSaturation = 4.0;

    /// <summary>HSV value floor for a light-mode surface; light mode is literally light (dark panels stay in dark mode).</summary>
    private const double MinSurfaceValue = 70.0;

    /// <summary>Minimum HSV saturation for a light-mode wallpaper token; the light layer must stay colourful.</summary>
    private const double MinWallpaperSaturation = 12.0;

    /// <summary>Maximum circular hue distance (degrees) between a wallpaper token and its dark twin.</summary>
    private const double MaxWallpaperHueDrift = 20.0;

    /// <summary>The logo hue band (degrees, HSV) each palette's accents must sit in.</summary>
    private static readonly (string Id, double Min, double Max)[] HueBands =
    {
        ("original", 35, 55),
        ("ocean", 195, 230),
        ("mint", 130, 175),
        ("coral", 5, 32),
        ("graphite-gold", 35, 55),
    };

    [Fact]
    public void AccentTokens_CarryTheLogoHueOfTheirPalette_InBothModes()
    {
        foreach (var (id, min, max) in HueBands)
        {
            var palette = ThemeCatalog.Default.Get(id);
            Assert.Equal(id, palette.Id);

            foreach (var mode in new[] { ResolvedMode.Light, ResolvedMode.Dark })
            {
                var tokens = palette.For(mode);
                AssertHue(tokens.Primary, min, max, $"{id}/{mode} primary");
                AssertHue(tokens.PrimaryHover, min, max, $"{id}/{mode} primaryHover");
                AssertHue(tokens.AccentSoft, min, max, $"{id}/{mode} accent (mint)");
            }
        }
    }

    [Fact]
    public void LightStructuralLines_MeetTheHairlineContrastFloor()
    {
        foreach (var palette in ThemeCatalog.Default.Palettes)
        {
            var tokens = palette.Light;
            AssertLine(tokens.Border, tokens.Surface, $"{palette.Id}/light card and row borders on the card surface");
            AssertLine(tokens.Border, tokens.Canvas, $"{palette.Id}/light dividers on the canvas");
            AssertLine(tokens.Border, tokens.Field, $"{palette.Id}/light input outlines on the field fill");
            AssertLine(tokens.DropBorder, tokens.Drop, $"{palette.Id}/light drop-zone outline on the drop fill");
        }
    }

    [Fact]
    public void LightSurfaces_CarryTheirPalettesTint_NotWhite()
    {
        foreach (var (id, min, max) in HueBands)
        {
            var tokens = ThemeCatalog.Default.Get(id).Light;
            foreach (var (name, color) in new[]
            {
                ("canvas", tokens.Canvas),
                ("sidebar", tokens.Sidebar),
                ("card (surface)", tokens.Surface),
                ("field", tokens.Field),
            })
            {
                var saturation = Saturation(color);
                Assert.True(saturation >= MinSurfaceSaturation, $"{id}/light {name} {ToHex(color)} has saturation {saturation:F1}%, below {MinSurfaceSaturation}%");
                AssertHue(color, min, max, $"{id}/light {name}");
            }
        }
    }

    [Fact]
    public void LightSurfaces_AreLightAndTinted_NotDarkPanels()
    {
        foreach (var (id, min, max) in HueBands)
        {
            var tokens = ThemeCatalog.Default.Get(id).Light;
            var gradient = tokens.SidebarGradient;
            Assert.Equal(2, gradient.Count);
            Assert.True(Value(gradient[0]) > Value(gradient[1]), $"{id}/light sidebarGradient has no depth: {ToHex(gradient[0])} -> {ToHex(gradient[1])}");

            foreach (var (name, color) in new[]
            {
                ("canvas", tokens.Canvas),
                ("sidebar", tokens.Sidebar),
                ("sidebarGradient[0]", gradient[0]),
                ("sidebarGradient[1]", gradient[1]),
                ("card (surface)", tokens.Surface),
                ("field", tokens.Field),
            })
            {
                var value = Value(color);
                Assert.True(value >= MinSurfaceValue, $"{id}/light {name} {ToHex(color)} has HSV value {value:F1}%, below {MinSurfaceValue}% - a light palette must not ship a dark panel");
                var saturation = Saturation(color);
                Assert.True(saturation >= MinSurfaceSaturation, $"{id}/light {name} {ToHex(color)} has saturation {saturation:F1}%, below {MinSurfaceSaturation}%");
                AssertHue(color, min, max, $"{id}/light {name}");
            }
        }
    }

    [Fact]
    public void PreviewMockupTokens_CarryTheirPaletteTint_InBothModes()
    {
        foreach (var (id, min, max) in HueBands)
        {
            var palette = ThemeCatalog.Default.Get(id);
            foreach (var mode in new[] { ResolvedMode.Light, ResolvedMode.Dark })
            {
                var tokens = palette.For(mode);
                foreach (var (name, color) in new[]
                {
                    ("previewStrip", tokens.PreviewStrip),
                    ("previewTrim", tokens.PreviewTrim),
                    ("previewPlate", tokens.PreviewPlate),
                })
                {
                    var where = $"{id}/{mode} {name}";
                    var saturation = Saturation(color);
                    Assert.True(saturation >= MinSurfaceSaturation, $"{where} {ToHex(color)} is untinted: saturation {saturation:F1}%, below {MinSurfaceSaturation}%");
                    AssertHue(color, min, max, where);
                    var value = Value(color);
                    if (mode == ResolvedMode.Light)
                    {
                        Assert.True(value >= MinSurfaceValue, $"{where} {ToHex(color)} has HSV value {value:F1}%, below {MinSurfaceValue}% - the light taskbar mockup must be light");
                    }
                    else
                    {
                        Assert.True(value < MinSurfaceValue, $"{where} {ToHex(color)} has HSV value {value:F1}%, not dark - the dark taskbar mockup must stay dark");
                    }
                }
            }
        }
    }

    [Fact]
    public void WallpaperKeepsItsPaletteHueFamily_AcrossModes()
    {
        foreach (var palette in ThemeCatalog.Default.Palettes)
        {
            AssertWallpaperPairs(palette, "desktopGradient");
            AssertWallpaperPairs(palette, "waves");
        }
    }

    private static void AssertWallpaperPairs(ThemePalette palette, string gradient)
    {
        var light = palette.Light.Gradients[gradient];
        var dark = palette.Dark.Gradients[gradient];
        Assert.Equal(dark.Count, light.Count);
        for (var i = 0; i < light.Count; i++)
        {
            var (lightStop, darkStop) = (light[i], dark[i]);
            Assert.True(Saturation(lightStop) >= MinWallpaperSaturation, $"{palette.Id}/light {gradient}[{i}] {ToHex(lightStop)} is bleached: saturation {Saturation(lightStop):F1}%, below {MinWallpaperSaturation}%");
            Assert.True(Luminance(lightStop) > Luminance(darkStop), $"{palette.Id} {gradient}[{i}]: light {ToHex(lightStop)} is not lighter than dark {ToHex(darkStop)}");
            if (Saturation(darkStop) < MinWallpaperSaturation)
            {
                continue; // a near-neutral dark stop (graphite-gold's greys) has no meaningful hue to match
            }

            var drift = HueDrift(lightStop, darkStop);
            Assert.True(drift <= MaxWallpaperHueDrift, $"{palette.Id} {gradient}[{i}]: light {ToHex(lightStop)} drifts {drift:F1}deg from dark {ToHex(darkStop)} (hue {Hue(lightStop):F1} vs {Hue(darkStop):F1}), over {MaxWallpaperHueDrift}deg");
        }
    }

    private static void AssertHue(Color color, double min, double max, string what)
    {
        var hue = Hue(color);
        Assert.True(hue >= min && hue <= max, $"{what} {ToHex(color)} has hue {hue:F1}, outside {min}-{max}");
    }

    private static void AssertLine(Color line, Color surface, string what)
    {
        var contrast = Contrast(line, surface);
        Assert.True(contrast >= MinLineContrast, $"{what}: {ToHex(line)} on {ToHex(surface)} is {contrast:F2}:1, below {MinLineContrast}:1");
    }

    private static double Hue(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta <= 0)
        {
            return 0;
        }

        var hue = max == r
            ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    /// <summary>HSV saturation of a colour in percent (0-100).</summary>
    private static double Saturation(Color color)
    {
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        return max == 0 ? 0 : (max - min) * 100.0 / max;
    }

    /// <summary>HSV value (brightness) of a colour in percent (0-100).</summary>
    private static double Value(Color color) => Math.Max(color.R, Math.Max(color.G, color.B)) * 100.0 / 255.0;

    /// <summary>Circular distance between two hues in degrees (0-180).</summary>
    private static double HueDrift(Color a, Color b)
    {
        var drift = Math.Abs(Hue(a) - Hue(b));
        return drift > 180 ? 360 - drift : drift;
    }

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

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
