using System.Drawing;
using System.Runtime.InteropServices;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class GroupLnkWriterTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void Create_ThenResolve_RoundTripsTargetArgumentsAumidAndIcon()
    {
        var exePath = NotepadPath();
        var iconPath = Path.Combine(_temp.Path, "custom.ico");
        WriteRedTestIcon(iconPath, 32);

        var result = GroupLnkWriter.Create("My Group", exePath, _temp.Path, iconPath);

        Assert.True(result.Success, result.Error);
        var expectedPath = Path.Combine(_temp.Path, "My Group.lnk");
        Assert.Equal(expectedPath, result.LnkPath, ignoreCase: true);
        Assert.True(File.Exists(expectedPath));

        // Read back through the resolver: target, AUMID and icon location must survive.
        var info = LnkResolver.Resolve(expectedPath);
        Assert.NotNull(info);
        Assert.Equal(exePath, info!.TargetPath, ignoreCase: true);
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("My Group"), info.AppUserModelId);
        Assert.Equal(iconPath, info.IconLocation, ignoreCase: true);
        Assert.Equal(0, info.IconIndex);

        // The resolver has no arguments field; the raw .lnk states args + working directory.
        var raw = ReadShortcut(expectedPath);
        Assert.Equal(GroupLnkWriter.BuildLaunchArguments("My Group"), raw.Arguments);
        Assert.Equal("\"My Group\"", raw.Arguments);
        Assert.Equal(Path.GetDirectoryName(exePath), raw.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void Create_WithoutIcon_FallsBackToTheExeIcon()
    {
        var exePath = NotepadPath();

        var result = GroupLnkWriter.Create("Alpha", exePath, _temp.Path);

        Assert.True(result.Success, result.Error);
        var info = LnkResolver.Resolve(result.LnkPath!);
        Assert.NotNull(info);
        Assert.Equal(exePath, info!.IconLocation, ignoreCase: true);
        Assert.Equal(0, info.IconIndex);
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), info.AppUserModelId);
    }

    [Fact]
    public void Create_MissingOutputDirectory_IsCreated()
    {
        var nested = Path.Combine(_temp.Path, "pins", "nested");

        var result = GroupLnkWriter.Create("Alpha", NotepadPath(), nested);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(nested, "Alpha.lnk")));
    }

    [Fact]
    public void Create_ExistingLink_IsReplacedInPlace()
    {
        // Legacy ShellLink saved with fRemember: true, which overwrites an existing .lnk.
        var firstIcon = Path.Combine(_temp.Path, "first.ico");
        var secondIcon = Path.Combine(_temp.Path, "second.ico");
        WriteRedTestIcon(firstIcon, 32);
        WriteRedTestIcon(secondIcon, 32);
        Assert.True(GroupLnkWriter.Create("Alpha", NotepadPath(), _temp.Path, firstIcon).Success);

        var second = GroupLnkWriter.Create("Alpha", NotepadPath(), _temp.Path, secondIcon);

        Assert.True(second.Success, second.Error);
        var info = LnkResolver.Resolve(second.LnkPath!);
        Assert.NotNull(info);
        Assert.Equal(secondIcon, info!.IconLocation, ignoreCase: true);
        Assert.Single(Directory.GetFiles(_temp.Path, "*.lnk"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bad@Name")]
    [InlineData("under_score")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void Create_InvalidGroupName_FailsWithoutWritingAnything(string? name)
    {
        var result = GroupLnkWriter.Create(name!, NotepadPath(), _temp.Path);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Null(result.LnkPath);
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void Create_TooLongGroupName_Fails()
    {
        var name = new string('a', Group.MaxNameLength + 1);

        var result = GroupLnkWriter.Create(name, NotepadPath(), _temp.Path);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void Create_WhenOutputDirectoryIsAFile_FailsCleanly()
    {
        var blocked = Path.Combine(_temp.Path, "not-a-directory");
        File.WriteAllText(blocked, "x");

        var result = GroupLnkWriter.Create("Alpha", NotepadPath(), blocked);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Null(result.LnkPath);
        Assert.Equal("x", File.ReadAllText(blocked));
    }

    [Fact]
    public void Create_WithStore_RejectsUnknownGroupWithoutWritingALink()
    {
        var store = new GroupStore(_temp.Path);

        var result = GroupLnkWriter.Create("Ghost", NotepadPath(), _temp.Path, groupStore: store);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.LnkPath);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "Ghost.lnk")));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.lnk"));
    }

    [Fact]
    public void Create_WithStore_WritesForAnExistingGroup()
    {
        var store = new GroupStore(_temp.Path);
        var saved = store.Save(new Group { Name = "Alpha" });
        Assert.True(saved.Success, saved.Error);

        var result = GroupLnkWriter.Create("Alpha", NotepadPath(), _temp.Path, groupStore: store);

        Assert.True(result.Success, result.Error);
        var info = LnkResolver.Resolve(result.LnkPath!);
        Assert.NotNull(info);
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), info!.AppUserModelId);
    }

    [Fact]
    public void DeriveAppUserModelId_IsStableAndUniquePerGroup()
    {
        var first = GroupLnkWriter.DeriveAppUserModelId("My Group");

        Assert.Equal(first, GroupLnkWriter.DeriveAppUserModelId("My Group"));
        Assert.Equal("Dockaroo.menu.My_Group", first);
        Assert.DoesNotContain(' ', first);
        Assert.Equal(AppConstants.AppUserModelIdBase + ".menu.", first[..(first.Length - "My_Group".Length)]);
        Assert.NotEqual(first, GroupLnkWriter.DeriveAppUserModelId("My Game"));
        Assert.NotEqual(
            GroupLnkWriter.DeriveAppUserModelId("Alpha"),
            GroupLnkWriter.DeriveAppUserModelId("Beta"));
    }

    [Fact]
    public void DeriveAppUserModelId_RejectsInvalidName()
    {
        Assert.Throws<ArgumentException>(() => GroupLnkWriter.DeriveAppUserModelId("Bad@Name"));
        Assert.Throws<ArgumentException>(() => GroupLnkWriter.DeriveAppUserModelId(string.Empty));
        Assert.Throws<ArgumentException>(() => GroupLnkWriter.BuildLaunchArguments("Bad@Name"));
    }

    [Fact]
    public void CreatedArguments_ParseBackToTheGroupNameThroughTheSecondInstancePayloadPath()
    {
        var created = GroupLnkWriter.Create("My Group", NotepadPath(), _temp.Path);
        Assert.True(created.Success, created.Error);

        // Windows splits the .lnk arguments the way the shell does at launch, and the second
        // instance joins its command-line arguments with spaces (Program.cs), so the group name
        // must come back whole.
        var commandLineArguments = SplitCommandLine(ReadShortcut(created.LnkPath!).Arguments);
        var recoveredGroupName = string.Join(' ', commandLineArguments);
        Assert.Equal("My Group", recoveredGroupName);

        // Feed the recovered name into the handoff payload shape the resident parses:
        // "popup:<x>:<y>:<group-name>" with the group name as everything after the third colon.
        var payload = $"popup:42:24:{recoveredGroupName}";
        Assert.StartsWith("popup:", payload, StringComparison.Ordinal);
        var rest = payload["popup:".Length..];
        var separatorX = rest.IndexOf(':');
        var separatorY = rest.IndexOf(':', separatorX + 1);
        Assert.True(separatorX > 0 && separatorY > separatorX);
        Assert.Equal("My Group", rest[(separatorY + 1)..]);
    }

    [Fact]
    public void ApplyCurrentProcessAppUserModelId_AcceptsAGroupIdAndRejectsBlank()
    {
        Assert.True(GroupLnkWriter.ApplyCurrentProcessAppUserModelId(GroupLnkWriter.DeriveAppUserModelId("Alpha")));
        Assert.False(GroupLnkWriter.ApplyCurrentProcessAppUserModelId(" "));
    }

    private static string NotepadPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(path), "notepad.exe was expected in the system directory.");
        return path;
    }

    private static (string TargetPath, string Arguments, string WorkingDirectory, string IconLocation) ReadShortcut(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(shellType);
        dynamic shell = Activator.CreateInstance(shellType!)!;
        dynamic shortcut = shell.CreateShortcut(lnkPath);
        try
        {
            return (
                (string)shortcut.TargetPath,
                (string)shortcut.Arguments,
                (string)shortcut.WorkingDirectory,
                (string)shortcut.IconLocation);
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)shortcut);
            Marshal.FinalReleaseComObject((object)shell);
        }
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var arguments = new string[count];
            for (var index = 0; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static void WriteRedTestIcon(string path, int size)
    {
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Red);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var stream = File.Create(path);
            icon.Save(stream);
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
        out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
