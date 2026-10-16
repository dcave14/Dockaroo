// Popup capture: the popup at 100/150/200% (injected metrics) plus the editor at 100%.
// Rules (TESTING.md): the editor is shown OFF-SCREEN at (-32000,-32000) with SW_SHOWNOACTIVATE,
// never activated, never TopMost; capture via PrintWindow only; all data in a temp root.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Icons;

internal static class Program
{
    private const int SwShowNoActivate = 4;

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.GetTempPath();
        var log = Path.Combine(outDir, "popup-capture.txt");
        var root = Path.Combine(Path.GetTempPath(), "dockaroo-popupcap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            var store = new GroupStore(Path.Combine(root, "config"));
            var group = new Group { Name = "Visual Gate", Width = 6 };
            var sys = Environment.SystemDirectory;
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Path.Combine(sys, "notepad.exe"), Name = "notepad" });
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Path.Combine(sys, "cmd.exe"), Name = "cmd" });
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Name = "explorer" });
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows), Name = "Windows folder" });
            group.Shortcuts.Add(new ShortcutEntry { TargetPath = Path.Combine(sys, "taskmgr.exe"), Name = "taskmgr" });
            var saved = store.Save(group);
            if (!saved.Success) throw new InvalidOperationException(saved.Error);

            using var icons = new IconCache(Path.Combine(root, "icon-cache"));
            var app = Assembly.Load("Dockaroo");
            var pinType = app.GetType("Dockaroo.App.PinnedStateCache", true);
            Func<string, bool?> probe = _ => null;
            var pins = Activator.CreateInstance(pinType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { Path.Combine(root, "Shortcuts"), Path.Combine(root, "Pinned"), Path.Combine(sys, "notepad.exe"), probe }, null);
            var editorType = app.GetType("Dockaroo.App.Editor.EditorForm", true);
            var sw = Stopwatch.StartNew();
            var editor = (Form)Activator.CreateInstance(editorType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { store, true, pins, Path.Combine(root, "Shortcuts"), icons, null }, null);
            sw.Stop();
            File.AppendAllText(log, $"editor ctor {sw.ElapsedMilliseconds} ms{Environment.NewLine}");

            editor.StartPosition = FormStartPosition.Manual;
            editor.ShowInTaskbar = false;
            editor.TopMost = false;
            editor.Location = new Point(-32000, -32000);
            ShowWindow(editor.Handle, SwShowNoActivate);

            // Pump until every row has an icon (posted from the thread pool).
            var list = editorType.GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
            var rowImage = list.GetType().GetMethod("RowImage", BindingFlags.Instance | BindingFlags.NonPublic);
            var wait = Stopwatch.StartNew();
            var loaded = 0;
            while (wait.ElapsedMilliseconds < 15000)
            {
                Application.DoEvents();
                loaded = 0;
                for (var i = 0; i < group.Shortcuts.Count; i++)
                {
                    if (rowImage.Invoke(list, new object[] { i }) != null) loaded++;
                }

                if (loaded == group.Shortcuts.Count) break;
                System.Threading.Thread.Sleep(20);
            }

            File.AppendAllText(log, $"icons delivered {loaded}/{group.Shortcuts.Count} after {wait.ElapsedMilliseconds} ms{Environment.NewLine}");
            editor.Refresh();
            Application.DoEvents();
            GetWindowRect(editor.Handle, out var r);
            using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(editor.Handle, hdc, 2); }
                finally { g.ReleaseHdc(hdc); }
            }

            var png = Path.Combine(outDir, "editor-100.png");
            bmp.Save(png, ImageFormat.Png);
            File.AppendAllText(log, $"captured {bmp.Width}x{bmp.Height} -> {png}{Environment.NewLine}");
            editor.Dispose();

            // Popups: same group, real icons preloaded at each scale's size.
            var popupType = app.GetType("Dockaroo.App.PopupForm", true);
            var metricsType = typeof(Dockaroo.Core.Popup.PopupMetrics);
            foreach (var dpi in new[] { 96, 144, 192 })
            {
                var metrics = Dockaroo.Core.Popup.PopupMetrics.ForDpi(dpi);
                icons.Preload(group.Shortcuts.Select(e => IconSource.FromPath(e.TargetPath)).ToList(), metrics.IconSize);
                var popup = (Form)Activator.CreateInstance(popupType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new object[] { group, icons, new Point(200, 200), null, metrics, null }, null);
                popup.TopMost = false;
                popup.Location = new Point(-32000, -32000);
                ShowWindow(popup.Handle, SwShowNoActivate);
                // Hover the second plate so the scaled plate shape is visible in the capture.
                var tile = Dockaroo.Core.Popup.PopupGeometry.TileBounds(1, group.Width, metrics);
                popupType.GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(popup, new object[] { new MouseEventArgs(MouseButtons.None, 0, tile.Left + 2, tile.Top + 2, 0) });
                GetWindowRect(popup.Handle, out var pr);
                using var pbmp = new Bitmap(pr.Right - pr.Left, pr.Bottom - pr.Top, PixelFormat.Format32bppArgb);
                // A layered (Opacity < 1) window needs a composed DWM frame before
                // PW_RENDERFULLCONTENT returns content; pump and retry until it is not all black.
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    popup.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                    using (var g = Graphics.FromImage(pbmp))
                    {
                        var hdc = g.GetHdc();
                        try { PrintWindow(popup.Handle, hdc, 2); }
                        finally { g.ReleaseHdc(hdc); }
                    }

                    if (pbmp.GetPixel(pbmp.Width / 2, 1).ToArgb() != Color.Black.ToArgb()) break;
                }

                var ppng = Path.Combine(outDir, $"popup-{dpi * 100 / 96}.png");
                pbmp.Save(ppng, ImageFormat.Png);
                File.AppendAllText(log, $"popup {dpi} dpi: client {popup.ClientSize.Width}x{popup.ClientSize.Height}, captured {pbmp.Width}x{pbmp.Height} -> {ppng}{Environment.NewLine}");
                popup.Dispose();
            }

            return loaded == group.Shortcuts.Count ? 0 : 1;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
