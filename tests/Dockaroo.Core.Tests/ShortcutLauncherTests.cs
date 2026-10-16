using Dockaroo.Core;
using Dockaroo.Core.Launch;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class ShortcutLauncherTests
{
    [Fact]
    public void CreateStartInfo_ComposesTargetArgumentsAndWorkingDirectory()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\Games\Game1.exe",
            Name = "Game One",
            Arguments = "--fullscreen",
            WorkingDirectory = @"C:\Games\Saves",
        };

        var startInfo = ShortcutLauncher.CreateStartInfo(entry);

        Assert.Equal(entry.TargetPath, startInfo.FileName);
        Assert.Equal("--fullscreen", startInfo.Arguments);
        Assert.Equal(@"C:\Games\Saves", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_EmptyWorkingDirectory_FallsBackToTargetDirectory()
    {
        var entry = new ShortcutEntry { TargetPath = @"C:\Games\Game1.exe", Name = "Game One" };

        var startInfo = ShortcutLauncher.CreateStartInfo(entry);

        Assert.Equal(@"C:\Games", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_WhitespaceWorkingDirectory_FallsBackToTargetDirectory()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\Games\Game1.exe",
            Name = "Game One",
            WorkingDirectory = "   ",
        };

        var startInfo = ShortcutLauncher.CreateStartInfo(entry);

        Assert.Equal(@"C:\Games", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_EmptyArguments_StayEmpty()
    {
        var entry = new ShortcutEntry { TargetPath = @"C:\Games\Game1.exe", Name = "Game One" };

        var startInfo = ShortcutLauncher.CreateStartInfo(entry);

        Assert.Equal(string.Empty, startInfo.Arguments);
    }

    [Fact]
    public void CreateStartInfo_StoreAppTarget_UsesAppsFolderAlias()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            Name = "Calculator",
        };

        var startInfo = ShortcutLauncher.CreateStartInfo(entry);

        Assert.Equal(@"shell:appsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", startInfo.FileName);
    }

    [Fact]
    public void Launch_ShortLivedProcess_ReportsStarted()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Name = "Command Prompt",
            Arguments = "/c exit 0",
        };

        var result = ShortcutLauncher.Launch(entry);

        Assert.True(result.Started, result.Error);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Launch_MissingTarget_ReturnsFailureWithoutThrowing()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\dockaroo-missing\definitely-not-here.exe",
            Name = "Missing",
        };

        var result = ShortcutLauncher.Launch(entry);

        Assert.False(result.Started);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Launch_InvalidEntry_ReturnsFailureWithoutLaunching()
    {
        var entry = new ShortcutEntry { TargetPath = string.Empty, Name = "No Target" };

        var result = ShortcutLauncher.Launch(entry);

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Launch_NullEntry_ReturnsFailure()
    {
        var result = ShortcutLauncher.Launch(null);

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
    }
}
