#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dockaroo.Core;
using Dockaroo.Core.Linking;

namespace PerfProbe;

/// <summary>
/// Writes a scratch data root the way a real one looks: &lt;root&gt;/config/&lt;Group&gt;/group.xml
/// with shortcuts whose targets are real .lnk files (&lt;root&gt;/Shortcuts/&lt;Group&gt;/...), plus
/// one pin-shaped &lt;Group&gt;.lnk per group in &lt;root&gt;/Shortcuts (the file PinnedStateCache
/// probes). Every .lnk targets an existing system executable for realistic icon extraction;
/// none of them is ever launched by the seed.
/// </summary>
internal static class Seeding
{
    public static (int Groups, int EntryLnks, int PinLnks) WriteRoot(string root, int groups, int shortcuts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (groups < 1 || shortcuts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(groups), "groups and shortcuts must be positive");
        }

        var targets = TargetExes();
        var shortcutsDir = Path.Combine(root, "Shortcuts");
        var store = new GroupStore(Path.Combine(root, "config"));
        var entryLnks = 0;
        var pinLnks = 0;
        for (var g = 1; g <= groups; g++)
        {
            var name = "Perf " + g.ToString(CultureInfo.InvariantCulture);
            var group = new Group { Name = name, Width = 6 };
            for (var s = 0; s < shortcuts; s++)
            {
                var exe = targets[((g * shortcuts) + s) % targets.Count];
                var exeDir = Path.GetDirectoryName(exe)!;
                var baseName = Path.GetFileNameWithoutExtension(exe);
                var lnk = Path.Combine(shortcutsDir, name, $"{s + 1:00} {baseName}.lnk");
                Interop.WriteShortcut(lnk, exe, string.Empty, exeDir, Interop.SwShowNormal);
                group.Shortcuts.Add(new ShortcutEntry
                {
                    TargetPath = lnk,
                    Name = baseName.Length <= ShortcutEntry.MaxNameLength ? baseName : baseName[..ShortcutEntry.MaxNameLength],
                    WorkingDirectory = exeDir,
                });
                entryLnks++;
            }

            // Overwrite: seeding a tester-owned root twice must stay idempotent.
            var saved = store.Save(group, overwrite: true);
            if (!saved.Success)
            {
                throw new InvalidOperationException($"seeding group '{name}' failed: {saved.Error}");
            }

            var pin = GroupLnkWriter.Create(name, targets[g % targets.Count], shortcutsDir, groupStore: store);
            if (!pin.Success)
            {
                throw new InvalidOperationException($"seeding pin shortcut for '{name}' failed: {pin.Error}");
            }

            pinLnks++;
        }

        return (groups, entryLnks, pinLnks);
    }

    private static IReadOnlyList<string> TargetExes()
    {
        var system = Environment.SystemDirectory;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Path.Combine(system, "notepad.exe"),
            Path.Combine(system, "cmd.exe"),
            Path.Combine(system, "taskmgr.exe"),
            Path.Combine(system, "winver.exe"),
            Path.Combine(system, "control.exe"),
            Path.Combine(system, "regedit.exe"),
            Path.Combine(system, "magnify.exe"),
            Path.Combine(system, "osk.exe"),
            Path.Combine(windows, "explorer.exe"),
            Path.Combine(system, "mspaint.exe"),
        };
        var existing = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0)
        {
            throw new InvalidOperationException("no system executables found for seeding");
        }

        return existing;
    }
}
