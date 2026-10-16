namespace Dockaroo.App;

/// <summary>
/// Startup gate between the pipe server and the tray context. The pipe server starts before the
/// startup preload, so a request can arrive while groups and icons are still being loaded:
/// those requests are queued here in arrival order (never dropped, never shown twice) and
/// replayed the moment preload finishes. Requests that arrive after preload go straight to the
/// show callback, exactly as before. The preload work and the show callback are constructor
/// injected so tests can hold preload open on a gate - no sleeping - and then assert both the
/// queue contents and the show calls.
/// </summary>
internal sealed class StartupPopupQueue
{
    private readonly Action _preload;
    private readonly Action<PopupRequest> _show;
    private readonly object _lock = new();
    private readonly Queue<PopupRequest> _pending = new();
    private Action<PopupRequest>? _dispatch;

    public StartupPopupQueue(Action preload, Action<PopupRequest> show)
    {
        _preload = preload ?? throw new ArgumentNullException(nameof(preload));
        _show = show ?? throw new ArgumentNullException(nameof(show));
    }

    /// <summary>Requests waiting for preload to finish (test and diagnostics surface).</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>True once preload finished; every later request is shown immediately.</summary>
    public bool IsReady
    {
        get
        {
            lock (_lock)
            {
                return _dispatch is not null;
            }
        }
    }

    /// <summary>
    /// Accepts one request: queued while preload runs, shown immediately after it; the request
    /// is never dropped.
    /// </summary>
    public void Post(PopupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Action<PopupRequest>? dispatch;
        lock (_lock)
        {
            if (_dispatch is null)
            {
                _pending.Enqueue(request);
                return;
            }

            dispatch = _dispatch;
        }

        dispatch(request);
    }

    /// <summary>
    /// Runs the preload work (blocking) and then replays every queued request in arrival order.
    /// </summary>
    public void PreloadAndReplay()
    {
        _preload();
        Complete();
    }

    /// <summary>Marks preload finished and replays queued requests; later calls do nothing.</summary>
    public void Complete()
    {
        PopupRequest[] replay;
        lock (_lock)
        {
            if (_dispatch is not null)
            {
                return;
            }

            _dispatch = _show;
            replay = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var request in replay)
        {
            _show(request);
        }
    }
}
