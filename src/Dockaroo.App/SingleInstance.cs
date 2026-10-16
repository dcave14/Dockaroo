using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Dockaroo.Core;

namespace Dockaroo.App;

/// <summary>One popup request, parsed from a handoff payload or raised by the tray menu.</summary>
/// <param name="Cursor">Cursor position captured by the second instance; only meaningful when <paramref name="HasCursor"/> is true.</param>
/// <param name="GroupName">Group to show; null asks for the first configured group.</param>
/// <param name="HasCursor">True when <paramref name="Cursor"/> carries a captured position.</param>
internal sealed record PopupRequest(Point Cursor, string? GroupName, bool HasCursor)
{
    /// <summary>The clicked taskbar and button, located off the UI thread; null = not a taskbar click.</summary>
    public TaskbarAnchor? Taskbar { get; init; }

    /// <summary>True for a plain start of the exe: open the editor instead of a popup.</summary>
    public bool OpenEditor { get; init; }
}

/// <summary>Client side of the single-instance handoff: build the payload, signal the resident process and return.</summary>
internal static class SingleInstance
{
    /// <summary>Payload that asks the resident instance to show the popup for the first group.</summary>
    public const string PopupPayload = "popup";

    /// <summary>Payload that asks the resident instance to open the editor (a plain start of the exe).</summary>
    public const string EditorPayload = "editor";

    private const string PopupPrefix = "popup:";
    private const int ConnectTimeoutMs = 5000;

    /// <summary>
    /// Builds the handoff payload: "popup:&lt;x&gt;:&lt;y&gt;:&lt;group-name&gt;". Without a
    /// group name the legacy bare payload is used and the resident picks the first group. The
    /// name is the whole remainder after the coordinates, so it may contain spaces and colons.
    /// </summary>
    public static string BuildPopupPayload(Point cursor, string? groupName)
    {
        if (string.IsNullOrEmpty(groupName))
        {
            return PopupPayload;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{PopupPrefix}{cursor.X}:{cursor.Y}:{groupName}");
    }

    /// <summary>
    /// Parses a payload. Accepted forms: "editor" (open the editor), "popup" (legacy, no
    /// cursor and no name) and "popup:&lt;x&gt;:&lt;y&gt;:&lt;anything&gt;". Never throws;
    /// malformed input returns false.
    /// </summary>
    public static bool TryParsePopupPayload(string? payload, out PopupRequest request)
    {
        request = new PopupRequest(Point.Empty, null, false);
        if (string.IsNullOrEmpty(payload))
        {
            return false;
        }

        if (payload == EditorPayload)
        {
            request = request with { OpenEditor = true };
            return true;
        }

        if (payload == PopupPayload)
        {
            return true;
        }

        if (!payload.StartsWith(PopupPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = payload[PopupPrefix.Length..];
        var separatorX = rest.IndexOf(':');
        if (separatorX <= 0)
        {
            return false;
        }

        var separatorY = rest.IndexOf(':', separatorX + 1);
        if (separatorY < 0)
        {
            return false;
        }

        if (!int.TryParse(rest.AsSpan(0, separatorX), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(rest.AsSpan(separatorX + 1, separatorY - separatorX - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        var name = rest[(separatorY + 1)..];
        request = new PopupRequest(new Point(x, y), name.Length == 0 ? null : name, true);
        return true;
    }

    /// <summary>
    /// The request this process's own command line asks for: the group's popup, or - for a plain
    /// start of the exe without a group name - the editor, so double-clicking Dockaroo.exe
    /// actually opens the app. The args
    /// are turned into the exact payload the handoff client sends and parsed back by the same
    /// parser, so a pinned taskbar button that starts the resident after a reboot still shows
    /// its popup on that first click. The cursor position is captured at launch time.
    /// </summary>
    /// <param name="args">Raw command line of the resident process.</param>
    /// <param name="cursor">Captured cursor position; the real mouse position by default, supplied explicitly by tests.</param>
    public static PopupRequest? RequestFromLaunchArguments(string[] args, Point? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var groupName = string.Join(' ', args);
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return new PopupRequest(Point.Empty, null, false) { OpenEditor = true };
        }

        var payload = BuildPopupPayload(cursor ?? Cursor.Position, groupName);
        return TryParsePopupPayload(payload, out var request) ? request : null;
    }

    /// <summary>
    /// Sends one payload line to the resident instance. Returns false when no instance
    /// answered within the timeout (the caller still exits).
    /// </summary>
    public static bool TrySend(string payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".", PipeNaming.CurrentPipeName(), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeoutMs);

            using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(payload);
            writer.Flush();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Server side: a single pipe listener loop that lives as long as the resident instance. The
/// pipe name carries the session id and user SID (see <see cref="PipeNaming"/>), so only this
/// session's processes can reach it. When the name is temporarily unavailable - a stale
/// listener, another process holding the single instance, or a denied access - construction
/// fails; the listener waits out a backoff and retries instead of spinning a core, and any
/// failure leaves the loop alive.
/// </summary>
internal sealed class SingleInstanceServer : IDisposable
{
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _onPayload;
    private readonly Func<string> _pipeName;
    private readonly Action? _onConstructionAttempt;
    private readonly TimeSpan _retryDelay;
    private readonly Task _listenTask;

    public SingleInstanceServer(Action<string> onPayload)
        : this(onPayload, PipeNaming.CurrentPipeName, onConstructionAttempt: null, retryDelay: DefaultRetryDelay)
    {
    }

    /// <summary>
    /// Test seam: <paramref name="pipeNameProvider"/> supplies the name for every attempt,
    /// <paramref name="onConstructionAttempt"/> is raised just before each construction so a
    /// test can timestamp attempts, and <paramref name="retryDelay"/> is the backoff after a
    /// failed construction.
    /// </summary>
    internal SingleInstanceServer(
        Action<string> onPayload,
        Func<string> pipeNameProvider,
        Action? onConstructionAttempt,
        TimeSpan retryDelay)
    {
        _onPayload = onPayload ?? throw new ArgumentNullException(nameof(onPayload));
        _pipeName = pipeNameProvider ?? throw new ArgumentNullException(nameof(pipeNameProvider));
        _onConstructionAttempt = onConstructionAttempt;
        _retryDelay = retryDelay;
        _listenTask = Task.Run(ListenAsync);
    }

    /// <summary>The listener loop, exposed so tests can verify it neither returned nor faulted.</summary>
    internal Task ListenTask => _listenTask;

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                _onConstructionAttempt?.Invoke();
                server = new NamedPipeServerStream(
                    _pipeName(),
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                // The name is taken (a stale listener of this same session+user, or another
                // process holding the single allowed instance) or the pipe could not be created
                // right now. Fall through to the backoff: never an immediate retry.
            }
            catch (UnauthorizedAccessException)
            {
                // Access to the name was denied. Same backoff; the listener keeps living.
            }

            if (server is null)
            {
                if (!await BackOffAsync().ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            try
            {
                using (server)
                {
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? payload = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(payload))
                    {
                        _onPayload(payload);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The client went away before the payload arrived; back off a beat, keep listening.
                if (!await BackOffAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Same treatment as a failed construction: wait, then listen again.
                if (!await BackOffAsync().ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    /// <summary>Waits one retry interval; false when the listener is shutting down.</summary>
    private async Task<bool> BackOffAsync()
    {
        try
        {
            await Task.Delay(_retryDelay, _cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listenTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Shutdown races are expected; nothing useful to do.
        }

        _cts.Dispose();
    }
}
