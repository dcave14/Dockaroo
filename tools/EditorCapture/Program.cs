// Editor capture: the editor and Appearance page in several palettes/modes and two window sizes.
// Off-screen at (-32000,-32000), SW_SHOWNOACTIVATE, PrintWindow; temp data only; never activated.
// Usage: EditorCapture <outDir>  (PNG files + editor-capture.txt log)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Appearance;
using Dockaroo.Core.Icons;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.GetTempPath();
        var log = Path.Combine(outDir, "editor-capture.txt");
        var root = Path.Combine(Path.GetTempPath(), "dockaroo-editorcap-" + Guid.NewGuid().ToString("N"));
        var runs = new List<(string Palette, ResolvedMode Mode, int W, int H, string Tag, bool Appearance)>
        {
            ("mint", ResolvedMode.Dark, 1440, 960, "mint-dark-1440", false),
            ("ocean", ResolvedMode.Light, 1440, 960, "ocean-light-1440", false),
            ("mint", ResolvedMode.Dark, 1440, 960, "appearance-mint-dark-1440", true),
            ("graphite-gold", ResolvedMode.Light, 1440, 960, "appearance-graphite-light-1440", true),
            ("coral", ResolvedMode.Dark, 1100, 800, "appearance-coral-dark-1100", true),
        };

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var store = new GroupStore(Path.Combine(root, "config"));
            var sys = Environment.SystemDirectory;
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Seed(store, "Daily workspace", 6, true, 10,
                (Path.Combine(win, "explorer.exe"), "Browser", "", ""),
                (Path.Combine(sys, "notepad.exe"), "Code editor", "--new-window", @"C:\Projects"),
                (win, "Files", "", ""),
                (Path.Combine(sys, "cmd.exe"), "Terminal", "", ""));
            Seed(store, "Browsers", 6, false, 10, (Path.Combine(win, "explorer.exe"), "Explorer", "", ""));
            Seed(store, "Gaming", 6, false, 10, (Path.Combine(sys, "taskmgr.exe"), "Task manager", "", ""));
            Seed(store, "Utilities", 6, false, 10, (Path.Combine(sys, "calc.exe"), "Calculator", "", ""));

            var app = Assembly.Load("Dockaroo");
            var themeType = app.GetType("Dockaroo.App.Editor.Theming.StaticThemeSource", true);
            var pinType = app.GetType("Dockaroo.App.PinnedStateCache", true);
            var editorType = app.GetType("Dockaroo.App.Editor.EditorForm", true);
            Func<string, bool?> probe = _ => null;
            using var icons = new IconCache(Path.Combine(root, "icon-cache"));

            foreach (var run in runs)
            {
                IDisposable theme;
                if (run.Appearance)
                {
                    // A real ThemeService over temp settings, so the Appearance page is writable.
                    var serviceType = app.GetType("Dockaroo.App.Appearance.ThemeService", true);
                    Func<bool> light = () => true;
                    var settingsStore = new AppearanceStore(Path.Combine(root, "appearance-" + run.Tag + ".json"));
                    theme = (IDisposable)Activator.CreateInstance(serviceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                        new object[] { settingsStore, null, light, false, null }, null);
                    serviceType.GetMethod("SetMode").Invoke(theme, new object[] { run.Mode == ResolvedMode.Dark ? AppMode.Dark : AppMode.Light });
                    serviceType.GetMethod("SetPalette").Invoke(theme, new object[] { run.Palette });
                }
                else
                {
                    theme = (IDisposable)Activator.CreateInstance(themeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { run.Palette, run.Mode }, null);
                }
                var pins = Activator.CreateInstance(pinType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new object[] { Path.Combine(root, "Shortcuts"), Path.Combine(root, "Pinned"), Path.Combine(sys, "notepad.exe"), probe }, null);
                var editor = (Form)Activator.CreateInstance(editorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new object[] { store, true, pins, Path.Combine(root, "Shortcuts"), icons, theme }, null);
                editor.StartPosition = FormStartPosition.Manual;
                editor.ShowInTaskbar = false;
                editor.Location = new Point(-32000, -32000);
                editor.Size = new Size(run.W, run.H);
                ShowWindow(editor.Handle, 4);

                // Sample state from the spec: "Daily workspace", second shortcut selected, one edit (dirty).
                editorType.GetMethod("LoadGroupByName", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, new object[] { "Daily workspace" });
                var entries = editorType.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
                entries.GetType().GetMethod("SelectIndex").Invoke(entries, new object[] { 1 });
                var width = editorType.GetField("_width", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
                width.GetType().GetProperty("Value").SetValue(width, 4);

                if (run.Appearance)
                {
                    editorType.GetMethod("ShowAppearancePage", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(editor, null);
                }

                Pump(40);
                var png = Path.Combine(outDir, $"editor-{run.Tag}.png");
                Capture(editor, png);
                File.AppendAllText(log, $"{run.Tag}: window {editor.Width}x{editor.Height} client {editor.ClientSize.Width}x{editor.ClientSize.Height} -> {png}{Environment.NewLine}");
                editor.Dispose();
                theme.Dispose();
            }

            return 0;
        }
        catch (Exception ex)
        {
            File.AppendAllText(log, "ERROR " + ex + Environment.NewLine);
            return 2;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Seed(GroupStore store, string name, int width, bool openAll, int opacity, params (string Path, string Name, string Args, string Wd)[] shortcuts)
    {
        var group = new Group { Name = name, Width = width, AllowOpenAll = openAll, Opacity = opacity };
        foreach (var s in shortcuts)
        {
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = s.Path, Name = s.Name, Arguments = s.Args, WorkingDirectory = s.Wd });
        }

        var result = store.Save(group);
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }

    private static void Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }
    }

    private static void Capture(Form form, string path)
    {
        GetWindowRect(form.Handle, out var r);
        using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            form.Refresh();
            Pump(2);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(form.Handle, hdc, 2); } finally { g.ReleaseHdc(hdc); }
            }

            if (bmp.GetPixel(bmp.Width / 2, bmp.Height / 2).ToArgb() != Color.Black.ToArgb()) break;
        }

        bmp.Save(path, ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
