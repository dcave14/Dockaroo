namespace Dockaroo.Core;

/// <summary>Shared identifiers used by the resident process and its handoff.</summary>
public static class AppConstants
{
    public const string AppName = "Dockaroo";
    public const string MutexName = @"Local\Dockaroo.SingleInstance";
    public const string PopupTitle = "Dockaroo Popup";

    /// <summary>
    /// Base of the per-group AppUserModelID; the full id is
    /// <c>&lt;base&gt;.menu.&lt;group_name&gt;</c> (the legacy scheme's ported shape, spaces as
    /// underscores). Because
    /// the base is the product name, renaming the product changes every id and breaks already
    /// pinned taskbar buttons.
    /// </summary>
    public const string AppUserModelIdBase = AppName;
}
