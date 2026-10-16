using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;

namespace Dockaroo.Core;

/// <summary>
/// Name of the single-instance handoff pipe. The name embeds the Windows session id and the
/// user SID because named pipes are machine-wide while the resident instance is per session and
/// user: under fast user switching every session+user gets its own pipe instead of fighting
/// over one name, and a second instance can never reach the wrong session's resident.
/// </summary>
public static class PipeNaming
{
    private const string PipePrefix = "Dockaroo";

    /// <summary>
    /// Pure name builder: <c>Dockaroo.&lt;sessionId&gt;.&lt;userSid&gt;</c>. The
    /// session id and SID come in as plain values, so this stays testable without a Windows
    /// identity lookup; a numeric session id and a SID contain only characters a pipe name
    /// accepts.
    /// </summary>
    public static string PipeNameFor(int sessionId, string userSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PipePrefix}.{sessionId}.{userSid}");
    }

    /// <summary>
    /// Pipe name for the current process: its session and the SID of the account it runs as.
    /// Both the handoff client and the resident's pipe server call this.
    /// </summary>
    public static string CurrentPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        return PipeNameFor(Process.GetCurrentProcess().SessionId, userSid);
    }
}
