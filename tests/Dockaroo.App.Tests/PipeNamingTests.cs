using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Dockaroo.Core;
using Xunit;

namespace Dockaroo.App.Tests;

public sealed class PipeNamingTests
{
    [Fact]
    public void PipeNameFor_EmbedsTheSessionIdAndTheSid()
    {
        var name = PipeNaming.PipeNameFor(7, "S-1-5-21-99-88-77");

        Assert.StartsWith("Dockaroo.7.S-1-5-21-99-88-77", name);
        Assert.Contains(".7.", name);
        Assert.Contains("S-1-5-21-99-88-77", name);
    }

    [Fact]
    public void PipeNameFor_DiffersForEverySessionAndSidCombination()
    {
        const string userA = "S-1-5-21-A";
        const string userB = "S-1-5-21-B";

        var sessionOneUserA = PipeNaming.PipeNameFor(1, userA);
        var sessionTwoUserA = PipeNaming.PipeNameFor(2, userA);
        var sessionOneUserB = PipeNaming.PipeNameFor(1, userB);

        Assert.NotEqual(sessionOneUserA, sessionTwoUserA);
        Assert.NotEqual(sessionOneUserA, sessionOneUserB);
        Assert.NotEqual(sessionTwoUserA, sessionOneUserB);

        Assert.Contains("1", sessionOneUserA);
        Assert.Contains("2", sessionTwoUserA);
        Assert.Contains(userA, sessionOneUserA);
        Assert.Contains(userB, sessionOneUserB);
    }

    [Fact]
    public void PipeNameFor_IsALegalPipeName()
    {
        var name = PipeNaming.PipeNameFor(3, "S-1-5-21-11-22-33");

        Assert.Matches(@"^[A-Za-z0-9.\-]+$", name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);

        // Windows itself accepts it: a server can take this name and a client can connect.
        using var server = new NamedPipeServerStream(
            name,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);

        client.Connect(timeout: 2000);

        Assert.True(client.IsConnected);
    }

    [Fact]
    public void CurrentPipeName_UsesTheCurrentProcessSessionAndUser()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var sessionId = Process.GetCurrentProcess().SessionId;

        var name = PipeNaming.CurrentPipeName();

        Assert.Equal(PipeNaming.PipeNameFor(sessionId, sid), name);
        Assert.Contains(sid, name);
        Assert.StartsWith($"Dockaroo.{sessionId}.", name);
    }
}
