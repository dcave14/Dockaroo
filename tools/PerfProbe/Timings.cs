#nullable enable
// The nine measured items of the PerfProbe harness (see Program.cs for the safety rules).
// Every item emits exactly one metric line (item4 emits one per DPI/cache variant).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Launch;
using Dockaroo.Core.Popup;
using static PerfProbe.Program;

namespace PerfProbe;

internal static class Timings
{
    private const int WarmIterations = 7;
    private const int SeedGroups = 5;
    private const int SeedShortcuts = 7;
    private const int PinRefreshGuardMs = 5000;
    private const int CmdExitBudgetMs = 10000;

    public static int Run(string root)
    {
        var process = Process.GetCurrentProcess();
        var peakStart = process.PeakWorkingSet64;

        // Item 0: seed the scratch root (same writer as --seed): configs + .lnk files.
        var t = Stopwatch.GetTimestamp();
        var (groups, entryLnks, pinLnks) = Seeding.WriteRoot(root, SeedGroups, SeedShortcuts);
        Line($"item0 seed {SeedGroups}x{SeedShortcuts}: {F(Ms(t))} ms ({groups} groups, {entryLnks} entry .lnk, {pinLnks} pin .lnk)");

        var shortcutsDir = Path.Combine(root, "Shortcuts");
        var iconCacheDir = Path.Combine(root, "icon-cache");

        // Item 1: GroupStore.LoadAll cold (fresh store) + warm (cache hit).
        var store = new GroupStore(Path.Combine(root, "config"));
        t = Stopwatch.GetTimestamp();
        var loaded = store.LoadAll();
        var coldLoadMs = Ms(t);
        var warmLoads = new double[WarmIterations];
        for (var i = 0; i < WarmIterations; i++)
        {
            t = Stopwatch.GetTimestamp();
            store.LoadAll();
            warmLoads[i] = Ms(t);
        }

        Line($"item1 groupstore.LoadAll: cold {F(coldLoadMs)} ms ({loaded.Count} groups); warm med {F(Median(warmLoads))} p95 {F(P95(warmLoads))} ms (n={WarmIterations})");
        if (loaded.Count != SeedGroups)
        {
            Fail($"item1 loaded {loaded.Count} groups, expected {SeedGroups}");
        }

        // Item 2: icon preload cold + the per-click warm lookup (FromPath + TryGet, memory only).
        using var icons = new IconCache(iconCacheDir);
        var allSources = loaded.SelectMany(g => g.Shortcuts).Select(e => IconSource.FromPath(e.TargetPath)).ToList();
        t = Stopwatch.GetTimestamp();
        var preloaded = icons.Preload(allSources, PopupMetrics.Default.IconSize);
        var preloadMs = Ms(t);
        // FromPath is rebuilt per iteration INSIDE the timed region: the label promises
        // "FromPath+TryGet", and FromPath's path-classification work is the per-click cost.
        var clickShortcuts = loaded[0].Shortcuts;
        var warmLookups = new double[WarmIterations];
        var hits = 0;
        for (var i = 0; i < WarmIterations; i++)
        {
            t = Stopwatch.GetTimestamp();
            hits = clickShortcuts.Count(e =>
            {
                var source = IconSource.FromPath(e.TargetPath);
                return icons.TryGet(source, PopupMetrics.Default.IconSize, out var result) && result is { IsMissing: false };
            });
            warmLookups[i] = Ms(t);
        }

        Line($"item2 iconcache.Preload cold: {F(preloadMs)} ms ({preloaded}/{allSources.Count} @{PopupMetrics.Default.IconSize}px); warm FromPath+TryGet {clickShortcuts.Count}: med {F(Median(warmLookups))} p95 {F(P95(warmLookups))} ms (n={WarmIterations}, hits {hits})");

        var app = Assembly.Load("Dockaroo");

        // Item 3: PinnedStateCache.Refresh (real IPinnedList3 probe) behind a 5 s guard. The cache
        // runs on a dedicated STA thread, like the resident's UI thread; on timeout the item
        // reports timeout:5000 and the abandoned background thread dies with the process.
        var pinsType = app.GetType("Dockaroo.App.PinnedStateCache", true)!;
        var pinsColdMs = -1.0;
        double[]? pinsWarm = null;
        string? pinsError = null;
        var refreshDone = new ManualResetEventSlim(false);
        var refreshThread = new Thread(() =>
        {
            try
            {
                var cache = Activator.CreateInstance(
                    pinsType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    args: new object?[] { shortcutsDir, Path.Combine(root, "Pinned"), Path.Combine(Environment.SystemDirectory, "notepad.exe"), null },
                    culture: null)!;
                var refresh = pinsType.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
                var groupsArg = new object?[] { loaded };
                var start = Stopwatch.GetTimestamp();
                refresh.Invoke(cache, groupsArg);
                pinsColdMs = Ms(start);
                var warm = new double[WarmIterations];
                for (var i = 0; i < WarmIterations; i++)
                {
                    start = Stopwatch.GetTimestamp();
                    refresh.Invoke(cache, groupsArg);
                    warm[i] = Ms(start);
                }

                pinsWarm = warm;
            }
            catch (Exception ex)
            {
                pinsError = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            }
            finally
            {
                refreshDone.Set();
            }
        });
        refreshThread.SetApartmentState(ApartmentState.STA);
        refreshThread.IsBackground = true;
        refreshThread.Start();
        if (!refreshDone.Wait(PinRefreshGuardMs))
        {
            Line($"item3 pins.Refresh: timeout:{PinRefreshGuardMs} (hang suspect hit)");
        }
        else if (pinsError is not null)
        {
            Line($"item3 pins.Refresh: error {pinsError}");
            Fail("item3 pins.Refresh threw");
        }
        else
        {
            Line($"item3 pins.Refresh: cold {F(pinsColdMs)} ms; warm med {F(Median(pinsWarm!))} p95 {F(P95(pinsWarm!))} ms (n={WarmIterations})");
        }

        // Item 4: PopupForm ctor + handle create (first UpdateLayer) + explicit UpdateLayer round
        // trip through the LayerUpToDate seam, at injected 96/144 DPI, warm and cold icon cache.
        var popupType = app.GetType("Dockaroo.App.PopupForm", true)!;
        var popupCtor = popupType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
        var updateLayer = popupType.GetMethod("UpdateLayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var layerProperty = popupType.GetProperty("LayerUpToDate", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var popupGroup = loaded[0];
        foreach (var dpi in new[] { 96, 144 })
        {
            var metrics = PopupMetrics.ForDpi(dpi);
            icons.Preload(popupGroup.Shortcuts.Select(e => IconSource.FromPath(e.TargetPath)).ToList(), metrics.IconSize);
            MeasurePopup(popupCtor, updateLayer, layerProperty, popupGroup, icons, metrics, dpi, "warm");
            using var coldIcons = new IconCache(Path.Combine(iconCacheDir, "cold-" + dpi));
            MeasurePopup(popupCtor, updateLayer, layerProperty, popupGroup, coldIcons, metrics, dpi, "cold");
        }

        // Item 5: TaskbarLocator.Locate logic cost (findButton:false is the no-UIA path; the
        // internal static ElementRectAt seam cannot be swapped out from outside) plus ONE live
        // read-only UIA rect read of the real taskbar. No clicks, no focus change.
        var locatorType = app.GetType("Dockaroo.App.TaskbarLocator", true)!;
        var elementRectAt = locatorType.GetMethod("ElementRectAt", BindingFlags.Static | BindingFlags.NonPublic)!;
        var locate = locatorType.GetMethod("Locate", BindingFlags.Static | BindingFlags.Public)!;
        var screen = Screen.PrimaryScreen!;
        var probe = ProbePoint(screen.Bounds, screen.WorkingArea);
        t = Stopwatch.GetTimestamp();
        var element = (Rectangle?)elementRectAt.Invoke(null, new object?[] { probe });
        var uiaMs = Ms(t);
        var locateMs = new double[WarmIterations];
        object? anchor = null;
        for (var i = 0; i < WarmIterations; i++)
        {
            t = Stopwatch.GetTimestamp();
            anchor = locate.Invoke(null, new object?[] { probe, false });
            locateMs[i] = Ms(t);
        }

        Line($"item5 taskbarlocator: uia-live {(element.HasValue ? Rect(element.Value) : "null")} in {F(uiaMs)} ms @({probe.X},{probe.Y}); locate logic med {F(Median(locateMs))} p95 {F(P95(locateMs))} ms (n={WarmIterations}, anchor {AnchorText(anchor)})");

        // Item 6: ShortcutLauncher.Launch of a scratch .lnk -> cmd.exe /c exit, IShellLink show
        // command SW_HIDE. The probe asserts no cmd.exe outlives the run.
        var systemDir = Environment.SystemDirectory;
        var launchLnk = Path.Combine(root, "launch", "hidden-exit.lnk");
        Interop.WriteShortcut(launchLnk, Path.Combine(systemDir, "cmd.exe"), "/c exit", systemDir, Interop.SwHide);
        var baseline = CmdPids();
        var spawnMs = new double[WarmIterations];
        var settleMs = new double[WarmIterations];
        var started = 0;
        var seen = 0;
        for (var i = 0; i < WarmIterations; i++)
        {
            var entry = new ShortcutEntry { TargetPath = launchLnk, Name = "perf-cmd", WorkingDirectory = systemDir };
            t = Stopwatch.GetTimestamp();
            var result = ShortcutLauncher.Launch(entry);
            spawnMs[i] = Ms(t);
            if (result.Started)
            {
                started++;
            }
            else
            {
                Line($"item6 launch failure: {result.Error}");
            }

            settleMs[i] = WaitForNewCmdGone(baseline, out var saw);
            if (saw)
            {
                seen++;
            }
        }

        var leftover = CmdPids().Count(pid => !baseline.Contains(pid));
        Line($"item6 shortcutlaunch: started {started}/{WarmIterations}; spawn med {F(Median(spawnMs))} p95 {F(P95(spawnMs))} ms; settle med {F(Median(settleMs))} p95 {F(P95(settleMs))} ms (cmd seen {seen}/{WarmIterations}); leftover cmd {leftover}");
        if (started != WarmIterations || leftover != 0)
        {
            Fail("item6 launch or leftover-cmd invariant");
        }

        // Item 7: environment probe, OpenExisting only - the mutex is never created, held or released.
        var mutexField = typeof(AppConstants).GetField(nameof(AppConstants.MutexName), BindingFlags.Public | BindingFlags.Static)!;
        var mutexName = (string)mutexField.GetValue(null)!;
        var mutexState = "absent";
        try
        {
            using var existing = Mutex.OpenExisting(mutexName);
            mutexState = "opened";
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            mutexState = "absent";
        }
        catch (UnauthorizedAccessException)
        {
            mutexState = "access-denied";
        }

        Line($"item7 mutex: {mutexState} ({mutexName})");

        // Item 8: peak working-set delta of this harness process across the phases above.
        process.Refresh();
        var peakEnd = process.PeakWorkingSet64;
        Line($"item8 harness peak ws delta: +{F((peakEnd - peakStart) / 1048576.0)} MB ({F(peakStart / 1048576.0)} -> {F(peakEnd / 1048576.0)} MB peak)");

        return Failed ? 1 : 0;
    }

    private static void MeasurePopup(
        ConstructorInfo ctor,
        MethodInfo updateLayer,
        PropertyInfo layerProperty,
        Group group,
        IconCache cache,
        PopupMetrics metrics,
        int dpi,
        string kind)
    {
        var ctorMs = new double[WarmIterations];
        var handleMs = new double[WarmIterations];
        var relayerMs = new double[WarmIterations];
        var layersOk = 0;
        for (var i = 0; i < WarmIterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            var popup = (Form)ctor.Invoke(new object?[] { group, cache, new Point(200, 200), null, metrics, null });
            ctorMs[i] = Ms(t);

            // The ctor sets TopMost; drop it before the handle exists. The form is never shown.
            popup.TopMost = false;
            popup.ShowInTaskbar = false;
            popup.StartPosition = FormStartPosition.Manual;
            popup.Location = new Point(-32000, -32000);

            t = Stopwatch.GetTimestamp();
            _ = popup.Handle; // OnHandleCreated -> first UpdateLayer (UpdateLayeredWindow round trip)
            handleMs[i] = Ms(t);
            var ok = (bool)layerProperty.GetValue(popup)!;

            t = Stopwatch.GetTimestamp();
            updateLayer.Invoke(popup, null);
            relayerMs[i] = Ms(t);
            if (ok && (bool)layerProperty.GetValue(popup)!)
            {
                layersOk++;
            }

            popup.Dispose();
        }

        Line($"item4 popup {dpi}dpi {kind}: ctor med {F(Median(ctorMs))} p95 {F(P95(ctorMs))}; handle+layer med {F(Median(handleMs))} p95 {F(P95(handleMs))}; relayer med {F(Median(relayerMs))} p95 {F(P95(relayerMs))} ms; layer ok {layersOk}/{WarmIterations}");
        if (layersOk != WarmIterations)
        {
            Fail($"item4 popup {dpi}dpi {kind}: LayerUpToDate false");
        }
    }

    /// <summary>
    /// A point inside the real taskbar (its window rect clamped into the screen it sits on, so
    /// an auto-hidden bar's visible sliver still gets probed). Falls back to the reserved
    /// working-area strip when the shell does not answer. Read-only.
    /// </summary>
    private static Point ProbePoint(Rectangle bounds, Rectangle work)
    {
        if (Interop.TryTaskbarRect(out var bar))
        {
            var screen = Screen.FromRectangle(bar) ?? Screen.PrimaryScreen!;
            var area = screen.Bounds;
            return new Point(
                Math.Clamp(bar.Left + (bar.Width / 2), area.Left, area.Right - 1),
                Math.Clamp(bar.Top + (bar.Height / 2), area.Top, area.Bottom - 1));
        }

        var cx = bounds.Left + (bounds.Width / 2);
        var cy = bounds.Top + (bounds.Height / 2);
        if (work.Bottom < bounds.Bottom)
        {
            return new Point(cx, bounds.Bottom - 4);
        }

        if (work.Top > bounds.Top)
        {
            return new Point(cx, bounds.Top + 4);
        }

        if (work.Left > bounds.Left)
        {
            return new Point(bounds.Left + 4, cy);
        }

        if (work.Right < bounds.Right)
        {
            return new Point(bounds.Right - 4, cy);
        }

        // No reserved strip (auto-hidden bar): still probe the bottom edge.
        return new Point(cx, bounds.Bottom - 4);
    }

    private static string AnchorText(object? anchor)
    {
        if (anchor is null)
        {
            return "none";
        }

        var type = anchor.GetType();
        var bar = (Rectangle)type.GetProperty("Bar")!.GetValue(anchor)!;
        var found = (bool)type.GetProperty("ButtonFound")!.GetValue(anchor)!;
        return $"bar {Rect(bar)}, buttonFound={found}";
    }

    private static string Rect(Rectangle rect)
    {
        return $"({rect.X},{rect.Y},{rect.Width},{rect.Height})";
    }

    /// <summary>Polls until no cmd.exe from this launch is left (or the budget runs out).</summary>
    private static double WaitForNewCmdGone(HashSet<int> baseline, out bool saw)
    {
        saw = false;
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var hasNew = CmdPids().Any(pid => !baseline.Contains(pid));
            if (hasNew)
            {
                saw = true;
            }

            var elapsed = Ms(start);
            if (!hasNew && (saw || elapsed > 400))
            {
                return elapsed;
            }

            if (elapsed > CmdExitBudgetMs)
            {
                return elapsed;
            }

            Thread.Sleep(25);
        }
    }

    private static HashSet<int> CmdPids()
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("cmd"))
        {
            try
            {
                pids.Add(process.Id);
            }
            catch (InvalidOperationException)
            {
                // Exited between enumeration and Id; nothing to track.
            }
            finally
            {
                process.Dispose();
            }
        }

        return pids;
    }
}
