using System.Drawing;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace Dockaroo.Core;

/// <summary>
/// A taskbar group: a named set of shortcuts plus the size and appearance
/// settings the popup uses. Field semantics are ported from the legacy
/// client.Classes.Category (legacy-app Classes/Category.cs).
/// </summary>
[XmlRoot("Group")]
public class Group
{
    /// <summary>Maximum length of <see cref="Name"/> (legacy UI limit).</summary>
    public const int MaxNameLength = 49;

    /// <summary>Smallest shortcuts-per-row value.</summary>
    public const int MinWidth = 1;

    /// <summary>Largest shortcuts-per-row value (legacy UI limit).</summary>
    public const int MaxWidth = 20;

    /// <summary>Most transparent allowed opacity.</summary>
    public const int MaxOpacity = 100;

    /// <summary>
    /// Most transparent a popup is ever drawn (backlog P2-6). Stored values above it (legacy and
    /// imported groups may hold 100) stay as they are but render at this value, so a popup never
    /// becomes an invisible window that still takes clicks.
    /// </summary>
    public const int MaxRenderedTransparency = 90;

    /// <summary>Default dark popup color.</summary>
    public const string DarkColorHex = "#1F1F1F";

    /// <summary>Light popup color.</summary>
    public const string LightColorHex = "#E6E6E6";

    private static readonly Regex NamePattern = new("^[0-9a-zA-Z ]+$", RegexOptions.Compiled);
    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>User-visible group name; letters, digits and spaces only.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Number of shortcuts shown per row (1..20).</summary>
    public int Width { get; set; } = 6;

    /// <summary>True when Ctrl+Enter may open every shortcut at once.</summary>
    public bool AllowOpenAll { get; set; }

    /// <summary>Popup background color as "#RRGGBB".</summary>
    public string ColorHex { get; set; } = DarkColorHex;

    /// <summary>Transparency percentage: 0 = solid, 100 = fully transparent.</summary>
    public int Opacity { get; set; } = 10;

    /// <summary>Shortcuts in display order.</summary>
    [XmlArray("Shortcuts")]
    [XmlArrayItem("Shortcut")]
    public List<ShortcutEntry> Shortcuts { get; set; } = new();

    /// <summary>
    /// Deep copy of this group and every entry, so a caller can mutate the copy without
    /// touching the original or any cached instance. Pure allocation, never IO, so it stays
    /// safe on the popup show path.
    /// </summary>
    public Group Clone()
    {
        var clone = (Group)MemberwiseClone();
        var shortcuts = new List<ShortcutEntry>(Shortcuts?.Count ?? 0);
        if (Shortcuts is not null)
        {
            foreach (var entry in Shortcuts)
            {
                shortcuts.Add(entry is null ? null! : entry.Clone());
            }
        }

        clone.Shortcuts = shortcuts;
        return clone;
    }

    /// <summary>Checks the group and every entry; never throws.</summary>
    public bool TryValidate(out string? error)
    {
        if (!IsValidName(Name))
        {
            error = $"Name is required, must have no leading or trailing spaces, must be at most {MaxNameLength} characters, and may contain only letters, digits and spaces.";
            return false;
        }

        if (Width < MinWidth || Width > MaxWidth)
        {
            error = $"Width must be between {MinWidth} and {MaxWidth}.";
            return false;
        }

        if (Opacity < 0 || Opacity > MaxOpacity)
        {
            error = $"Opacity must be between 0 and {MaxOpacity}.";
            return false;
        }

        if (!IsValidColorHex(ColorHex))
        {
            error = "ColorHex must look like #RRGGBB.";
            return false;
        }

        if (Shortcuts is null)
        {
            error = "Shortcuts must not be null.";
            return false;
        }

        foreach (var entry in Shortcuts)
        {
            if (entry is null)
            {
                error = "Shortcuts must not contain null entries.";
                return false;
            }

            if (!entry.TryValidate(out var entryError))
            {
                error = entryError;
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Legacy name rules: not blank, at most 49 characters, letters/digits/spaces.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || name != name.Trim())
        {
            return false;
        }

        return NamePattern.IsMatch(name);
    }

    /// <summary>
    /// Formats <paramref name="color"/> as "#RRGGBB". Unlike ColorTranslator.ToHtml this never
    /// returns a colour name ("White"), which <see cref="IsValidColorHex"/> would reject.
    /// </summary>
    public static string ToColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Turns any HTML colour text ("#RRGGBB", "#RGB", "White", "Red") into "#RRGGBB".
    /// Returns false and <paramref name="hex"/> = <see cref="DarkColorHex"/> when the text is blank
    /// or not a colour. Never throws.
    /// </summary>
    public static bool TryNormalizeColorHex(string? text, out string hex)
    {
        if (IsValidColorHex(text))
        {
            hex = text!;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                var color = ColorTranslator.FromHtml(text.Trim());
                if (!color.IsEmpty && color.A == 255)
                {
                    hex = ToColorHex(color);
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
            }
        }

        hex = DarkColorHex;
        return false;
    }

    /// <summary>True when <paramref name="colorHex"/> is a "#RRGGBB" value.</summary>
    public static bool IsValidColorHex(string? colorHex)
    {
        return colorHex is not null && ColorPattern.IsMatch(colorHex);
    }
}
