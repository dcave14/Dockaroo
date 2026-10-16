using System.Drawing;
using Xunit;

namespace Dockaroo.App.Tests;

public sealed class StartupPopupQueueTests
{
    [Fact]
    public async Task RequestDuringPreload_IsQueuedAndShownOnlyAfterPreloadFinishes()
    {
        using var preloadEntered = new ManualResetEventSlim();
        using var releasePreload = new ManualResetEventSlim();
        var shown = new List<PopupRequest>();
        var queue = new StartupPopupQueue(
            preload: () =>
            {
                preloadEntered.Set();
                releasePreload.Wait(TimeSpan.FromSeconds(10));
            },
            show: request =>
            {
                lock (shown)
                {
                    shown.Add(request);
                }
            });

        var preload = Task.Run(queue.PreloadAndReplay);
        Assert.True(preloadEntered.Wait(TimeSpan.FromSeconds(10)), "the preload hook never ran");

        var during = new PopupRequest(new Point(10, 20), "PC STUFF", HasCursor: true);
        queue.Post(during);

        // Still preloading: the request waits in the queue, nothing was shown.
        Assert.Equal(1, queue.PendingCount);
        Assert.False(queue.IsReady);
        Assert.Empty(shown);

        releasePreload.Set();
        await preload.WaitAsync(TimeSpan.FromSeconds(10));

        // Preload finished: the queued request was replayed, with its content intact.
        Assert.True(queue.IsReady);
        Assert.Equal(0, queue.PendingCount);
        var replayed = Assert.Single(shown);
        Assert.Equal("PC STUFF", replayed.GroupName);
        Assert.Equal(new Point(10, 20), replayed.Cursor);
        Assert.True(replayed.HasCursor);
    }

    [Fact]
    public void RequestAfterPreload_IsShownImmediatelyAndNeverQueued()
    {
        var shown = new List<PopupRequest>();
        var queue = new StartupPopupQueue(preload: () => { }, show: shown.Add);

        queue.PreloadAndReplay();

        Assert.True(queue.IsReady);
        Assert.Empty(shown);

        var after = new PopupRequest(new Point(3, 4), "DEV", HasCursor: true);
        queue.Post(after);

        var replayed = Assert.Single(shown);
        Assert.Equal("DEV", replayed.GroupName);
        Assert.Equal(new Point(3, 4), replayed.Cursor);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void QueuedRequests_ReplayInArrivalOrderWithoutBeingDropped()
    {
        var shown = new List<PopupRequest>();
        var queue = new StartupPopupQueue(preload: () => { }, show: shown.Add);

        queue.Post(new PopupRequest(new Point(1, 1), "FIRST", HasCursor: true));
        queue.Post(new PopupRequest(new Point(2, 2), "SECOND", HasCursor: true));

        Assert.Equal(2, queue.PendingCount);
        Assert.Empty(shown);

        queue.PreloadAndReplay();

        Assert.Equal(2, shown.Count);
        Assert.Equal("FIRST", shown[0].GroupName);
        Assert.Equal(new Point(1, 1), shown[0].Cursor);
        Assert.Equal("SECOND", shown[1].GroupName);
        Assert.Equal(new Point(2, 2), shown[1].Cursor);
        Assert.Equal(0, queue.PendingCount);
    }
}
