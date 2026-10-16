using System.Diagnostics;
using System.IO.Pipes;
using Dockaroo.Core;
using Xunit;

namespace Dockaroo.App.Tests;

public sealed class SingleInstanceServerTests
{
    [Fact]
    public void OccupiedPipeName_BacksOffInsteadOfSpinningAndKeepsListening()
    {
        // Hold the exact production pipe name (current session + user) so every construction
        // attempt fails, like a stale listener or a same-user process holding the instance.
        var pipeName = PipeNaming.CurrentPipeName();
        using var occupier = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var attempts = new List<long>();
        var stopwatch = Stopwatch.StartNew();

        using var listener = new SingleInstanceServer(
            onPayload: _ => { },
            pipeNameProvider: () => pipeName,
            onConstructionAttempt: () => { lock (attempts) { attempts.Add(stopwatch.ElapsedMilliseconds); } },
            retryDelay: TimeSpan.FromMilliseconds(500));

        Thread.Sleep(2000);

        long[] timestamps;
        lock (attempts)
        {
            timestamps = attempts.ToArray();
        }

        // At most ~3 construction attempts per second over a 2 s window.
        Assert.True(timestamps.Length >= 2, $"expected retries on the busy name, saw {timestamps.Length} attempt(s)");
        Assert.True(timestamps.Length <= 6, $"expected at most 6 attempts in 2 s, saw {timestamps.Length}");

        for (var i = 1; i < timestamps.Length; i++)
        {
            var gap = timestamps[i] - timestamps[i - 1];
            Assert.True(gap >= 400, $"attempt {i + 1} came {gap} ms after the previous one; the listener must back off ~500 ms");
        }

        // The listener must have survived the failures: no throw (not faulted), no return.
        Assert.False(listener.ListenTask.IsFaulted, "the listener faulted on the occupied name");
        Assert.False(listener.ListenTask.IsCompleted, "the listener ended instead of carrying on");
    }
}
