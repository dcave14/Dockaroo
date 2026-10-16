using Dockaroo.Core;

namespace Dockaroo.App;

/// <summary>
/// Text for the tray balloons the resident raises instead of modal dialogs. Pure, so tests can
/// check the wording without a NotifyIcon.
/// </summary>
internal static class TrayMessages
{
    /// <summary>Windows truncates balloon text past this length.</summary>
    public const int MaxBalloonText = 255;

    /// <summary>One balloon for every shortcut that failed in a single click or Launch All.</summary>
    public static string LaunchFailures(IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var text = failures.Count switch
        {
            0 => string.Empty,
            1 => $"Could not start {failures[0]}",
            _ => $"{failures.Count} shortcuts could not be started:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}",
        };

        return Truncate(text);
    }

    /// <summary>
    /// Balloon for a popup request whose group could not be loaded (a renamed or deleted group
    /// behind a stale pinned button), or null when the group loaded.
    /// </summary>
    public static string? ForFailedLoad(string groupName, GroupLoadStatus status)
    {
        return status switch
        {
            GroupLoadStatus.NotFound => Truncate($"Group '{groupName}' no longer exists. If it was renamed, re-pin its taskbar button."),
            GroupLoadStatus.Corrupt => Truncate($"Group '{groupName}' could not be read. Open it in the editor and save it again."),
            _ => null,
        };
    }

    /// <summary>
    /// Menu text for a display name: a lone '&amp;' is a WinForms mnemonic marker, so
    /// "Graphite &amp; gold" would show as "Graphite  gold" unless doubled.
    /// </summary>
    public static string MenuLabel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Replace("&", "&&", StringComparison.Ordinal);
    }

    private static string Truncate(string text)
    {
        return text.Length <= MaxBalloonText ? text : string.Concat(text.AsSpan(0, MaxBalloonText - 3), "...");
    }
}
