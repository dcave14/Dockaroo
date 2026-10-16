namespace Dockaroo.Core.Appearance;

/// <summary>App-wide light/dark choice. Independent of every group's popup background.</summary>
public enum AppMode
{
    /// <summary>Follow the Windows app colour mode, live.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>The light/dark mode actually in effect after <see cref="AppMode.System"/> is resolved.</summary>
public enum ResolvedMode
{
    /// <summary>Light tokens.</summary>
    Light,

    /// <summary>Dark tokens.</summary>
    Dark,
}

/// <summary>
/// The user's appearance choice: a mode and one palette ID, which selects the app logo and its
/// complete colour tokens together. Persisted as stable strings (design handoff
/// implementation-spec.md), never labels or indices.
/// </summary>
public sealed record AppearancePreferences(AppMode Mode, string PaletteId)
{
    /// <summary>Default mode: follow Windows.</summary>
    public const AppMode DefaultMode = AppMode.System;

    /// <summary>Default palette: mint, the adopted application icon.</summary>
    public const string DefaultPaletteId = "mint";

    /// <summary>Every supported palette ID, in display order.</summary>
    public static IReadOnlyList<string> PaletteIds { get; } = new[] { "original", "ocean", "mint", "coral", "graphite-gold" };

    /// <summary>System mode with the mint palette.</summary>
    public static AppearancePreferences Default { get; } = new(DefaultMode, DefaultPaletteId);

    /// <summary>True for one of <see cref="PaletteIds"/> (exact, lower-case match).</summary>
    public static bool IsKnownPalette(string? paletteId)
    {
        return paletteId is not null && PaletteIds.Contains(paletteId, StringComparer.Ordinal);
    }

    /// <summary>The persisted string for a mode: "light", "dark" or "system".</summary>
    public static string ModeToString(AppMode mode)
    {
        return mode switch
        {
            AppMode.Light => "light",
            AppMode.Dark => "dark",
            _ => "system",
        };
    }

    /// <summary>Parses a persisted mode string; false for anything unknown.</summary>
    public static bool TryParseMode(string? text, out AppMode mode)
    {
        switch (text)
        {
            case "light":
                mode = AppMode.Light;
                return true;
            case "dark":
                mode = AppMode.Dark;
                return true;
            case "system":
                mode = AppMode.System;
                return true;
            default:
                mode = DefaultMode;
                return false;
        }
    }

    /// <summary>Resolves <see cref="Mode"/>; <paramref name="systemUsesLight"/> answers System.</summary>
    public ResolvedMode Resolve(bool systemUsesLight)
    {
        return Mode switch
        {
            AppMode.Light => ResolvedMode.Light,
            AppMode.Dark => ResolvedMode.Dark,
            _ => systemUsesLight ? ResolvedMode.Light : ResolvedMode.Dark,
        };
    }
}
