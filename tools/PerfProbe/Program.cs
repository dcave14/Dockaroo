#nullable enable
// PerfProbe: the perf-audit measurement harness (.opencode/tasks/perf-audit.md, round 1).
// Measures resident-preload and popup-show path components with Stopwatch (median/p95 over warm
// iterations) and prints one metric line per item 0-8 to stdout and to the report file passed as
// the first argument. `--seed <root> <groups> <shortcuts>` writes a scratch data root only (for
// the tester's portable spawn A/B). The timed items live in Timings.cs.
//
// Safety (TESTING.md; perf-audit task): scratch roots only under %TEMP%\dockaroo-perf-* and
// deleted on exit; %LOCALAPPDATA%\Dockaroo and the installed app folder are never written;
// TrayContext is never constructed (no tray icon); popup forms are created off-screen at
// (-32000,-32000), hidden and never activated; no input injection, no SystemSounds, no
// screenshots; the mutex probe is Mutex.OpenExisting only (never create/hold/release); the
// launched cmd.exe (`cmd /c exit` through a SW_HIDE shortcut) must leave no process behind.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PerfProbe;

internal static class Program
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static StreamWriter? _report;
    private static bool _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--seed", StringComparison.Ordinal))
        {
            return RunSeed(args);
        }

        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: PerfProbe <report-file>");
            Console.Error.WriteLine("       PerfProbe --seed <root> <groups> <shortcuts>");
            return 2;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "dockaroo-perf-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            _report = new StreamWriter(args[0], append: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cannot write the report file '{args[0]}': {ex.Message}");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(scratch);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Line($"PerfProbe scratch={scratch}");
            var exit = Timings.Run(scratch);
            Line("cleanup: scratch " + (TryDelete(scratch) ? "deleted" : "DELETE FAILED"));
            return exit;
        }
        catch (Exception ex)
        {
            Line("ERROR " + ex);
            TryDelete(scratch);
            return 2;
        }
        finally
        {
            _report?.Dispose();
        }
    }

    /// <summary>
    /// `--seed &lt;root&gt; &lt;groups&gt; &lt;shortcuts&gt;`: writes configs + .lnk files into a
    /// scratch root under %TEMP% (never anywhere else). No measurement, no launch.
    /// </summary>
    private static int RunSeed(string[] args)
    {
        if (args.Length != 4
            || !int.TryParse(args[2], NumberStyles.Integer, Inv, out var groups)
            || !int.TryParse(args[3], NumberStyles.Integer, Inv, out var shortcuts)
            || groups is < 1 or > 50
            || shortcuts is < 1 or > 100)
        {
            Console.Error.WriteLine("usage: PerfProbe --seed <root> <groups> <shortcuts>");
            return 2;
        }

        var root = Path.GetFullPath(args[1]);
        var temp = Path.GetFullPath(Path.GetTempPath());
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !leaf.StartsWith("dockaroo-perf", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("refusing: --seed root must be under %TEMP% and named dockaroo-perf*");
            return 2;
        }

        try
        {
            Directory.CreateDirectory(root);
            var (g, entryLnks, pinLnks) = Seeding.WriteRoot(root, groups, shortcuts);
            Console.WriteLine($"seeded {root}: {g} groups, {entryLnks} entry .lnk, {pinLnks} pin .lnk");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("seed failed: " + ex.Message);
            return 1;
        }
    }

    /// <summary>One metric/report line: stdout plus the report file (flushed per line).</summary>
    internal static void Line(string text)
    {
        Console.WriteLine(text);
        _report?.WriteLine(text);
    }

    internal static void Fail(string reason)
    {
        _failed = true;
        Line("FAIL " + reason);
    }

    internal static bool Failed => _failed;

    internal static double Ms(long startTicks)
    {
        return (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
    }

    internal static double Median(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    internal static double P95(double[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
        return sorted[index];
    }

    internal static string F(double value)
    {
        return value.ToString("0.###", Inv);
    }

    private static bool TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        return !Directory.Exists(path);
    }
}
