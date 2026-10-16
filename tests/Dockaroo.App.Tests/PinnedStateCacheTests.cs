using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dockaroo.Core;
using Dockaroo.Core.Icons;
using Dockaroo.Core.Linking;
using Xunit;

namespace Dockaroo.App.Tests;

/// <summary>
/// Content tests for the selected-state pin plumbing: the golden AUMID derivation, the
/// in-place AppUserModelID repair on temp .lnk copies (never on real user data) and the
/// PinnedStateCache decision logic against a fake pin probe plus real temp .lnk files.
/// </summary>
public sealed class PinnedStateCacheTests : IDisposable
{
    private readonly string _root;
    private readonly string _shortcuts;
    private readonly string _pinned;
    private readonly string _exePath;

    public PinnedStateCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Dockaroo.App.Tests", Guid.NewGuid().ToString("N"));
        _shortcuts = Path.Combine(_root, "Shortcuts");
        _pinned = Path.Combine(_root, "UserPinned");
        Directory.CreateDirectory(_shortcuts);
        Directory.CreateDirectory(_pinned);
        _exePath = NotepadPath();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a leftover folder is harmless.
        }
    }

    // ---------------------------------------------------------------- AUMID derivation and repair

    [Fact]
    public void AumidDerivation_IsTheGoldenMenuFormat()
    {
        Assert.Equal("Dockaroo.menu.My_Group", GroupLnkWriter.DeriveAppUserModelId("My Group"));
        Assert.Equal(AppConstants.AppUserModelIdBase + ".menu.My_Group", GroupLnkWriter.DeriveAppUserModelId("My Group"));
        Assert.Throws<ArgumentException>(() => GroupLnkWriter.DeriveAppUserModelId("Bad@Name"));
    }

    [Fact]
    public void CreatedLink_ReadsBackTheDerivedAppUserModelId()
    {
        var result = GroupLnkWriter.Create("Alpha", _exePath, _shortcuts);

        Assert.True(result.Success, result.Error);
        var info = LnkResolver.Resolve(result.LnkPath!);
        Assert.NotNull(info);
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), info!.AppUserModelId);
        Assert.Equal(_exePath, info.TargetPath, ignoreCase: true);
    }

    [Fact]
    public void EnsureAppUserModelId_FillsAMissingPropertyOnce()
    {
        var lnk = WriteBareShortcut(_shortcuts, "Alpha");
        Assert.Null(LnkResolver.Resolve(lnk)!.AppUserModelId);
        var before = Hash(lnk);

        Assert.True(GroupLnkWriter.EnsureAppUserModelId(lnk, GroupLnkWriter.DeriveAppUserModelId("Alpha")));
        var repaired = Hash(lnk);
        Assert.NotEqual(before, repaired);
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), LnkResolver.Resolve(lnk)!.AppUserModelId);

        // Idempotent: the second call finds the property and writes nothing.
        Assert.True(GroupLnkWriter.EnsureAppUserModelId(lnk, GroupLnkWriter.DeriveAppUserModelId("Alpha")));
        Assert.Equal(repaired, Hash(lnk));
        Assert.Single(Directory.GetFiles(_shortcuts, "*.lnk"));
    }

    [Fact]
    public void EnsureAppUserModelId_LeavesAPresentValueUntouched()
    {
        var created = GroupLnkWriter.Create("Alpha", _exePath, _shortcuts);
        var lnk = created.LnkPath!;
        var before = Hash(lnk);

        Assert.True(GroupLnkWriter.EnsureAppUserModelId(lnk, "Dockaroo.menu.Other"));

        Assert.Equal(before, Hash(lnk));
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), LnkResolver.Resolve(lnk)!.AppUserModelId);
    }

    [Fact]
    public void EnsureAppUserModelId_MissingFileOrBlankInput_IsFalse()
    {
        Assert.False(GroupLnkWriter.EnsureAppUserModelId(Path.Combine(_shortcuts, "Ghost.lnk"), "Dockaroo.menu.Ghost"));
        Assert.False(GroupLnkWriter.EnsureAppUserModelId(" ", "Dockaroo.menu.Ghost"));
        Assert.False(GroupLnkWriter.EnsureAppUserModelId(Path.Combine(_shortcuts, "Ghost.lnk"), " "));
    }

    // ---------------------------------------------------------------- pin state cache

    [Fact]
    public void Cache_FindsAPinThroughThePinnedListProbe()
    {
        CreateGroupLink("Alpha");
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => true);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });

        Assert.True(cache.HasPin("Alpha"));
        Assert.True(cache.TryGetPinnedAppUserModelId("Alpha", out var appUserModelId));
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), appUserModelId);
    }

    [Fact]
    public void Cache_FindsAPinnedCopyByItsAumid()
    {
        var link = CreateGroupLink("Alpha");
        File.Copy(link, Path.Combine(_pinned, "Renamed copy.lnk"));
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });

        Assert.True(cache.HasPin("Alpha"));
    }

    [Fact]
    public void Cache_IsUnpinnedWithoutAMatch()
    {
        CreateGroupLink("Alpha");
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });

        Assert.False(cache.HasPin("Alpha"));
        Assert.False(cache.HasPin("Ghost"));
        Assert.False(cache.TryGetPinnedAppUserModelId("Alpha", out var appUserModelId));
        Assert.Null(appUserModelId);
    }

    [Fact]
    public void Cache_RefreshesAStalePinAway()
    {
        var link = CreateGroupLink("Alpha");
        var pinnedCopy = Path.Combine(_pinned, "Alpha.lnk");
        File.Copy(link, pinnedCopy);
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });
        Assert.True(cache.HasPin("Alpha"));

        File.Delete(pinnedCopy);
        cache.Refresh(new[] { new Group { Name = "Alpha" } });
        Assert.False(cache.HasPin("Alpha"));

        File.Copy(link, pinnedCopy);
        cache.Refresh(new[] { new Group { Name = "Alpha" } });
        Assert.True(cache.HasPin("Alpha"));

        // A group that disappeared from the list is dropped from the cache.
        cache.Refresh(Array.Empty<Group>());
        Assert.False(cache.HasPin("Alpha"));
    }

    [Fact]
    public void Cache_RepairsAMissingAumidOnThePinnedCopy()
    {
        CreateGroupLink("Alpha");
        var pinnedCopy = WriteBareShortcut(_pinned, "Alpha");
        Assert.Null(LnkResolver.Resolve(pinnedCopy)!.AppUserModelId);
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });

        Assert.True(cache.HasPin("Alpha"));
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), LnkResolver.Resolve(pinnedCopy)!.AppUserModelId);
        Assert.True(File.Exists(pinnedCopy), "the pinned copy must never be renamed");
    }

    [Fact]
    public void Cache_RepairsAMissingAumidOnTheDataRootShortcut()
    {
        var dataRoot = WriteBareShortcut(_shortcuts, "Alpha");
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });

        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Alpha"), LnkResolver.Resolve(dataRoot)!.AppUserModelId);
        Assert.False(cache.HasPin("Alpha"));
    }

    [Fact]
    public void Cache_IgnoresAPinnedCopyWithADifferentAumid()
    {
        CreateGroupLink("Alpha");
        var beta = GroupLnkWriter.Create("Beta", _exePath, _pinned);
        Assert.True(beta.Success, beta.Error);
        var pinnedAlpha = Path.Combine(_pinned, "Alpha.lnk");
        File.Move(beta.LnkPath!, pinnedAlpha);
        var before = Hash(pinnedAlpha);
        var cache = new PinnedStateCache(_shortcuts, _pinned, _exePath, _ => null);

        cache.Refresh(new[] { new Group { Name = "Alpha" } });

        Assert.False(cache.HasPin("Alpha"));
        Assert.Equal(before, Hash(pinnedAlpha));
        Assert.Equal(GroupLnkWriter.DeriveAppUserModelId("Beta"), LnkResolver.Resolve(pinnedAlpha)!.AppUserModelId);
    }

    // ---------------------------------------------------------------- helpers

    private string CreateGroupLink(string name)
    {
        var result = GroupLnkWriter.Create(name, _exePath, _shortcuts);
        Assert.True(result.Success, result.Error);
        return result.LnkPath!;
    }

    /// <summary>Writes a bare WSH shortcut with no PKEY_AppUserModel_ID (the missing state).</summary>
    private string WriteBareShortcut(string directory, string name)
    {
        var path = Path.Combine(directory, name + ".lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(shellType);
        dynamic shell = Activator.CreateInstance(shellType!)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = _exePath;
                shortcut.Arguments = GroupLnkWriter.BuildLaunchArguments(name);
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject((object)shortcut);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject((object)shell);
        }

        return path;
    }

    private static byte[] Hash(string path)
    {
        return SHA256.HashData(File.ReadAllBytes(path));
    }

    private static string NotepadPath()
    {
        var path = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(File.Exists(path), "notepad.exe was expected in the system directory.");
        return path;
    }
}
