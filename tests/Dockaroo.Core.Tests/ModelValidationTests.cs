using System.Drawing;
using Dockaroo.Core;
using Xunit;

namespace Dockaroo.Core.Tests;

public sealed class ModelValidationTests
{
    [Fact]
    public void Group_Defaults_MatchLegacySemantics()
    {
        var group = new Group();

        Assert.Equal(6, group.Width);
        Assert.False(group.AllowOpenAll);
        Assert.Equal(Group.DarkColorHex, group.ColorHex);
        Assert.Equal(10, group.Opacity);
        Assert.Empty(group.Shortcuts);
    }

    [Fact]
    public void Group_WithoutName_IsInvalid()
    {
        var group = new Group();

        Assert.False(group.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Group_WithValidFields_IsValid()
    {
        var group = MakeValidGroup("My Group");

        Assert.True(group.TryValidate(out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Group 1")]
    [InlineData("abcXYZ 019")]
    public void Group_Name_AllowedValues(string name)
    {
        Assert.True(Group.IsValidName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("bad_name")]
    [InlineData("bad-name")]
    [InlineData("bad@name")]
    [InlineData("bad.name")]
    [InlineData("bad/name")]
    public void Group_Name_RejectedValues(string? name)
    {
        Assert.False(Group.IsValidName(name));
    }

    [Fact]
    public void Group_Name_Accepts49Characters()
    {
        var name = new string('a', Group.MaxNameLength);

        Assert.True(Group.IsValidName(name));
    }

    [Fact]
    public void Group_Name_Rejects50Characters()
    {
        var name = new string('a', Group.MaxNameLength + 1);

        Assert.False(Group.IsValidName(name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(20)]
    public void Group_Width_InsideBounds_Valid(int width)
    {
        var group = MakeValidGroup();
        group.Width = width;

        Assert.True(group.TryValidate(out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(21)]
    public void Group_Width_OutsideBounds_Invalid(int width)
    {
        var group = MakeValidGroup();
        group.Width = width;

        Assert.False(group.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(100)]
    public void Group_Opacity_InsideBounds_Valid(int opacity)
    {
        var group = MakeValidGroup();
        group.Opacity = opacity;

        Assert.True(group.TryValidate(out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Group_Opacity_OutsideBounds_Invalid(int opacity)
    {
        var group = MakeValidGroup();
        group.Opacity = opacity;

        Assert.False(group.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(Group.DarkColorHex)]
    [InlineData(Group.LightColorHex)]
    [InlineData("#a1b2c3")]
    public void Group_ColorHex_ValidValues(string colorHex)
    {
        Assert.True(Group.IsValidColorHex(colorHex));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("1F1F1F")]
    public void Group_ColorHex_InvalidValues(string? colorHex)
    {
        Assert.False(Group.IsValidColorHex(colorHex));
    }

    [Fact]
    public void Group_WithInvalidEntry_IsInvalid()
    {
        var group = MakeValidGroup();
        group.Shortcuts.Add(new ShortcutEntry
        {
            TargetPath = @"C:\Apps\Tool.exe",
            Name = new string('x', ShortcutEntry.MaxNameLength + 1),
        });

        Assert.False(group.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Group_WithNullEntry_IsInvalid()
    {
        var group = MakeValidGroup();
        group.Shortcuts.Add(null!);

        Assert.False(group.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ShortcutEntry_ValidEntry_IsValid()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\Apps\Tool.exe",
            Name = "Tool",
            Arguments = "--verbose",
            WorkingDirectory = @"C:\Apps",
        };

        Assert.True(entry.TryValidate(out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ShortcutEntry_Accepts27CharacterName()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\Apps\Tool.exe",
            Name = new string('x', ShortcutEntry.MaxNameLength),
        };

        Assert.True(entry.TryValidate(out var error));
        Assert.Null(error);
    }

    [Fact]
    public void ShortcutEntry_Rejects28CharacterName()
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\Apps\Tool.exe",
            Name = new string('x', ShortcutEntry.MaxNameLength + 1),
        };

        Assert.False(entry.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShortcutEntry_RejectsBlankName(string name)
    {
        var entry = new ShortcutEntry
        {
            TargetPath = @"C:\Apps\Tool.exe",
            Name = name,
        };

        Assert.False(entry.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShortcutEntry_RejectsBlankTargetPath(string targetPath)
    {
        var entry = new ShortcutEntry
        {
            TargetPath = targetPath,
            Name = "Tool",
        };

        Assert.False(entry.TryValidate(out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ShortcutEntry_Create_DerivesNameFromFileName()
    {
        var entry = ShortcutEntry.Create(@"C:\Program Files\Some App\someapp.exe");

        Assert.Equal(@"C:\Program Files\Some App\someapp.exe", entry.TargetPath);
        Assert.Equal("someapp", entry.Name);
        Assert.Equal(@"C:\Program Files\Some App", entry.WorkingDirectory);
    }

    [Fact]
    public void ShortcutEntry_Create_TruncatesLongFileNameTo27Characters()
    {
        var fileName = new string('n', 40) + ".exe";
        var entry = ShortcutEntry.Create(@"C:\Apps\" + fileName);

        Assert.Equal(ShortcutEntry.MaxNameLength, entry.Name.Length);
        Assert.Equal(new string('n', ShortcutEntry.MaxNameLength), entry.Name);
    }

    [Fact]
    public void ShortcutEntry_Create_UsesFolderItselfAsWorkingDirectory()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Dockaroo.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var entry = ShortcutEntry.Create(folder);

            Assert.Equal(folder, entry.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ShortcutEntry_Defaults_AreEmptyStrings()
    {
        var entry = new ShortcutEntry();

        Assert.Equal(string.Empty, entry.TargetPath);
        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Arguments);
        Assert.Equal(string.Empty, entry.WorkingDirectory);
    }

    [Fact]
    public void ToColorHex_NamedColours_GiveHexNotNames()
    {
        Assert.Equal("#FFFFFF", Group.ToColorHex(Color.White));
        Assert.Equal("#FF0000", Group.ToColorHex(Color.FromKnownColor(KnownColor.Red)));
        Assert.Equal("#0A0B0C", Group.ToColorHex(Color.FromArgb(10, 11, 12)));
        Assert.True(Group.IsValidColorHex(Group.ToColorHex(Color.CornflowerBlue)));
    }

    [Theory]
    [InlineData("White", "#FFFFFF")]
    [InlineData("red", "#FF0000")]
    [InlineData("#2a3b4c", "#2a3b4c")]
    [InlineData("#ABC", "#AABBCC")]
    public void TryNormalizeColorHex_ParsesHtmlColours(string text, string expected)
    {
        Assert.True(Group.TryNormalizeColorHex(text, out var hex));
        Assert.Equal(expected, hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NotAColour")]
    [InlineData("#GGHHII")]
    [InlineData("Transparent")]
    public void TryNormalizeColorHex_Garbage_FallsBackToDark(string? text)
    {
        Assert.False(Group.TryNormalizeColorHex(text, out var hex));
        Assert.Equal(Group.DarkColorHex, hex);
    }

    private static Group MakeValidGroup(string name = "Test Group")
    {
        return new Group
        {
            Name = name,
            Width = 6,
            AllowOpenAll = true,
            ColorHex = "#A1B2C3",
            Opacity = 40,
            Shortcuts =
            {
                new ShortcutEntry
                {
                    TargetPath = @"C:\Apps\Tool.exe",
                    Name = "Tool",
                    Arguments = "--verbose",
                    WorkingDirectory = @"C:\Apps",
                },
            },
        };
    }
}
