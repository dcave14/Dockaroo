using System.Drawing;
using Xunit;

namespace Dockaroo.App.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void PayloadRoundTrip_CarriesTheGroupNameAndCursorExactly()
    {
        var cursor = new Point(1234, 567);

        var payload = SingleInstance.BuildPopupPayload(cursor, "PC STUFF");

        Assert.Equal("popup:1234:567:PC STUFF", payload);
        Assert.True(SingleInstance.TryParsePopupPayload(payload, out var request));
        Assert.Equal("PC STUFF", request.GroupName);
        Assert.Equal(cursor, request.Cursor);
        Assert.True(request.HasCursor);
    }

    [Fact]
    public void PayloadRoundTrip_KeepsColonsInsideTheGroupName()
    {
        var payload = SingleInstance.BuildPopupPayload(new Point(-8, 12), "A:B C");

        Assert.Equal("popup:-8:12:A:B C", payload);
        Assert.True(SingleInstance.TryParsePopupPayload(payload, out var request));
        Assert.Equal("A:B C", request.GroupName);
        Assert.Equal(new Point(-8, 12), request.Cursor);
    }

    [Fact]
    public void BarePayload_AsksForTheFirstGroupWithoutACursor()
    {
        Assert.True(SingleInstance.TryParsePopupPayload("popup", out var request));
        Assert.Null(request.GroupName);
        Assert.False(request.HasCursor);
        Assert.Equal(Point.Empty, request.Cursor);
    }

    [Fact]
    public void LaunchArguments_ProduceOneRequestWithTheJoinedGroupAndTheGivenCursor()
    {
        var request = SingleInstance.RequestFromLaunchArguments(
            new[] { "PC", "STUFF" },
            cursor: new Point(77, 88));

        Assert.NotNull(request);
        Assert.Equal("PC STUFF", request!.GroupName);
        Assert.Equal(new Point(77, 88), request.Cursor);
        Assert.True(request.HasCursor);
    }

    [Fact]
    public void LaunchArguments_WithoutAGroupName_OpenTheEditor()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { " " } })
        {
            var request = SingleInstance.RequestFromLaunchArguments(args);

            Assert.NotNull(request);
            Assert.True(request!.OpenEditor);
            Assert.Null(request.GroupName);
            Assert.False(request.HasCursor);
        }
    }

    [Fact]
    public void EditorPayload_ParsesToAnEditorRequest_AndPopupPayloadsDoNot()
    {
        Assert.True(SingleInstance.TryParsePopupPayload(SingleInstance.EditorPayload, out var editor));
        Assert.True(editor.OpenEditor);

        Assert.True(SingleInstance.TryParsePopupPayload("popup", out var bare));
        Assert.False(bare.OpenEditor);
        Assert.True(SingleInstance.TryParsePopupPayload(SingleInstance.BuildPopupPayload(new Point(1, 2), "DEV"), out var group));
        Assert.False(group.OpenEditor);
        Assert.Equal("DEV", group.GroupName);
    }
}
