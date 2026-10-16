using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Dockaroo.Core;
using Dockaroo.Core.Linking;

namespace Dockaroo.App;

internal static class Program
{
    private const int AsfwAny = -1;
    private const string CreateLinkSwitch = "--create-link";
    private const int FailureExitCode = 1;
    private const int UsageExitCode = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length > 0 && string.Equals(args[0], CreateLinkSwitch, StringComparison.Ordinal))
        {
            return CreateLink(args);
        }

        using var mutex = new Mutex(initiallyOwned: false, AppConstants.MutexName, out bool createdNew);
        if (!createdNew)
        {
            // A resident instance already owns the name: capture the cursor, hand the request
            // over and exit right away. The foreground right is passed on because the resident
            // was started earlier and could not otherwise activate the popup for this click.
            string? groupName = args.Length == 0 ? null : string.Join(' ', args);
            if (!string.IsNullOrWhiteSpace(groupName) && Group.IsValidName(groupName))
            {
                // Launch-side parity with the legacy client: this short-lived second instance
                // carries the group's AppUserModelID so the pinned taskbar button shows the
                // running state while the handoff happens.
                _ = GroupLnkWriter.ApplyCurrentProcessAppUserModelId(GroupLnkWriter.DeriveAppUserModelId(groupName));
            }

            _ = AllowSetForegroundWindow(AsfwAny);
            SingleInstance.TrySend(SingleInstance.BuildPopupPayload(Cursor.Position, groupName));
            return 0;
        }

        // The resident takes its own command line as the first popup request: a pinned group
        // button that starts the resident after a reboot still shows its popup on that first
        // click. A plain start (no group name) asks for nothing, as before.
        var startupRequest = SingleInstance.RequestFromLaunchArguments(args);

        using var tray = new TrayContext();

        // The pipe server starts before preload so a second instance during startup is queued
        // and shown instead of being dropped. Every request goes through the same gate; the
        // resident's own startup request does too, so it is shown once preload finishes.
        var startupQueue = new StartupPopupQueue(tray.Preload, tray.PostShowPopup);
        using var server = new SingleInstanceServer(payload =>
        {
            if (SingleInstance.TryParsePopupPayload(payload, out var request))
            {
                // Pipe thread: find the clicked taskbar button here, never on the UI thread.
                startupQueue.Post(WithTaskbar(request));
            }
        });

        if (startupRequest is not null)
        {
            startupQueue.Post(WithTaskbar(startupRequest));
        }

        startupQueue.PreloadAndReplay();
        TaskbarLocator.WarmUp();

        Application.Run(tray);
        return 0;
    }

    /// <summary>
    /// <c>--create-link &lt;group&gt; [output-dir]</c>: writes the pinning shortcut for one
    /// group and exits (0 written, 1 invalid or unknown group or IO failure, 2 usage). Runs
    /// before the single-instance check, so creating a shortcut never hands off to the
    /// resident instance.
    /// </summary>
    private static int CreateLink(string[] args)
    {
        if (args.Length < 2 || args.Length > 3)
        {
            Console.Error.WriteLine("usage: Dockaroo.exe --create-link \"<group>\" [output-dir]");
            return UsageExitCode;
        }

        var dataRoot = DataRootResolver.ForCurrentProcess();
        var outputDirectory = args.Length == 3 ? args[2] : dataRoot.ShortcutsDirectory;
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            Console.Error.WriteLine("Could not resolve an output directory; pass one explicitly.");
            return FailureExitCode;
        }

        // The group must exist in the data root (config/<Group-Name>/group.xml, spaces become
        // underscores): a shortcut for an unknown group would open nothing, so it is rejected
        // instead of written.
        var store = new GroupStore(dataRoot.ConfigDirectory);

        // Same icon rule as the editor's "Create taskbar shortcut": the group's own GroupIcon.ico
        // when it has one, otherwise the exe icon.
        var groupIcon = Group.IsValidName(args[1]) ? Path.Combine(store.GetGroupDirectory(args[1]), "GroupIcon.ico") : null;
        var result = GroupLnkWriter.Create(
            args[1],
            Application.ExecutablePath,
            outputDirectory,
            iconPath: groupIcon is not null && File.Exists(groupIcon) ? groupIcon : null,
            groupStore: store);
        if (!result.Success)
        {
            Console.Error.WriteLine($"Could not create the shortcut: {result.Error}");
            return FailureExitCode;
        }

        Console.WriteLine(result.LnkPath);
        return 0;
    }

    private static PopupRequest WithTaskbar(PopupRequest request)
    {
        return request.HasCursor ? request with { Taskbar = TaskbarLocator.Locate(request.Cursor) } : request;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
