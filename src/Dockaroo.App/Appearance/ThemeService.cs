using System.Drawing;
using Microsoft.Win32;
using Dockaroo.App.Editor.Theming;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Appearance;

/// <summary>
/// Owns the app-wide appearance: the saved preferences, the light/dark mode in effect (System
/// follows the Windows app mode live), the palette's colour tokens and its logo icons. The
/// resident creates one on the UI thread and hands it to the tray and editor windows; it raises
/// <see cref="Changed"/> on that thread. Group popups never read it, so the popup show path is
/// unchanged.
/// </summary>
internal sealed class ThemeService : IEditorThemeSource, IAppearanceSettings, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const string IconResourcePrefix = "Dockaroo.App.Palettes.logo-";

    private readonly AppearanceStore _store;
    private readonly ThemeCatalog _catalog;
    private readonly Func<bool> _systemUsesLight;
    private readonly Action<Action>? _postToUi;
    private readonly bool _listening;
    private readonly Dictionary<(string Palette, int Size), Icon> _icons = new();
    private bool _disposed;

    /// <summary>
    /// Loads the saved preferences (defaults on a missing or bad file, see <see cref="LoadError"/>).
    /// <paramref name="systemUsesLight"/> replaces the registry read (tests);
    /// <paramref name="listenToSystem"/> subscribes to Windows colour-mode changes, which arrive
    /// on the SystemEvents thread and are handed to <paramref name="postToUi"/> (the resident
    /// passes its marshal control's BeginInvoke); required when listening.
    /// </summary>
    public ThemeService(
        AppearanceStore store,
        ThemeCatalog? catalog = null,
        Func<bool>? systemUsesLight = null,
        bool listenToSystem = true,
        Action<Action>? postToUi = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _catalog = catalog ?? ThemeCatalog.Default;
        _systemUsesLight = systemUsesLight ?? ReadSystemUsesLight;
        _postToUi = postToUi;

        var loaded = _store.Load();
        Preferences = loaded.Preferences;
        LoadError = loaded.Error;
        SystemUsesLight = _systemUsesLight();

        if (listenToSystem)
        {
            ArgumentNullException.ThrowIfNull(postToUi);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
    }

    /// <summary>Raised on the UI thread after the mode, palette or effective Windows mode changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The saved choice (System stays System; it is never replaced by the resolved mode).</summary>
    public AppearancePreferences Preferences { get; private set; }

    /// <summary>Why the saved file could not be used at startup; null when it loaded or was absent.</summary>
    public string? LoadError { get; }

    /// <summary>The Windows app mode from the last check (true = light).</summary>
    public bool SystemUsesLight { get; private set; }

    /// <summary>Light or dark, after resolving System.</summary>
    public ResolvedMode EffectiveMode => Preferences.Resolve(SystemUsesLight);

    /// <summary>The selected palette.</summary>
    public ThemePalette Palette => _catalog.Get(Preferences.PaletteId);

    /// <summary>Colour tokens for the selected palette in the effective mode.</summary>
    public ThemeTokens Tokens => Palette.For(EffectiveMode);

    /// <summary>Saves a new mode and applies it. Returns the error (nothing changes) or null.</summary>
    public string? SetMode(AppMode mode) => Apply(Preferences with { Mode = mode });

    /// <summary>Saves a new palette (logo + colours) and applies it. Returns the error or null.</summary>
    public string? SetPalette(string paletteId)
    {
        return AppearancePreferences.IsKnownPalette(paletteId)
            ? Apply(Preferences with { PaletteId = paletteId })
            : $"'{paletteId}' is not a known palette.";
    }

    /// <summary>
    /// The current palette's logo at <paramref name="size"/> (one frame of its multi-size ICO).
    /// Cached and owned by this service: callers must not dispose it.
    /// </summary>
    public Icon GetAppIcon(Size size) => GetIcon(Preferences.PaletteId, size);

    /// <summary>Any palette's logo at <paramref name="size"/>; cached and owned by this service.</summary>
    public Icon GetIcon(string paletteId, Size size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var palette = AppearancePreferences.IsKnownPalette(paletteId) ? paletteId : AppearancePreferences.DefaultPaletteId;
        var key = (palette, size.Width);
        if (!_icons.TryGetValue(key, out var icon))
        {
            using var stream = typeof(ThemeService).Assembly.GetManifestResourceStream(IconResourcePrefix + palette + ".ico")
                ?? throw new InvalidOperationException($"The '{palette}' logo resource is missing.");
            icon = new Icon(stream, size);
            _icons[key] = icon;
        }

        return icon;
    }

    /// <summary>Re-reads the Windows app mode; raises <see cref="Changed"/> when it flipped.</summary>
    public void RefreshSystemMode()
    {
        if (_disposed)
        {
            return;
        }

        var before = EffectiveMode;
        SystemUsesLight = _systemUsesLight();
        if (EffectiveMode != before)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_listening)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    /// <summary>Windows "Choose your app mode": AppsUseLightTheme (missing = light, the Windows default).</summary>
    internal static bool ReadSystemUsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    // Spec: on a failed save keep the last saved selection and report the error.
    private string? Apply(AppearancePreferences next)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (next == Preferences)
        {
            return null;
        }

        var error = _store.Save(next);
        if (error is not null)
        {
            return error;
        }

        Preferences = next;
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    // May be raised on the SystemEvents thread; Changed must reach listeners on the UI thread.
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _disposed)
        {
            return;
        }

        try
        {
            _postToUi!(RefreshSystemMode);
        }
        catch (InvalidOperationException)
        {
            // The marshal handle is gone: the resident is shutting down.
        }
    }
}
