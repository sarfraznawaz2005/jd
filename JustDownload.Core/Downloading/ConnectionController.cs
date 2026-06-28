namespace JustDownload.Core.Downloading;

/// <summary>Event args for a live connection-count change (TASK-027): the current desired and active counts.</summary>
public sealed class ConnectionCountChangedEventArgs : EventArgs
{
    /// <summary>Creates the event args with the current desired and active connection counts.</summary>
    public ConnectionCountChangedEventArgs(int desiredConnections, int activeConnections)
    {
        DesiredConnections = desiredConnections;
        ActiveConnections = activeConnections;
    }

    /// <summary>The target number of connections.</summary>
    public int DesiredConnections { get; }

    /// <summary>The number of connections currently transferring.</summary>
    public int ActiveConnections { get; }
}

/// <summary>
/// A live handle for adjusting and observing a download's connection count mid-flight (TASK-027, US-4).
/// The caller (UI) passes one into <see cref="ISegmentedDownloader.DownloadAsync"/> and then raises or
/// lowers <see cref="DesiredConnections"/> at any time: raising spawns extra connections via work-stealing,
/// lowering drains connections cleanly at segment boundaries. The engine writes back the live
/// <see cref="ActiveConnections"/> and raises <see cref="Changed"/> so the count is reflected in state and
/// events (AC2). Thread-safe.
/// </summary>
public sealed class ConnectionController
{
    private readonly object _gate = new();
    private int _desired;
    private int _active;

    /// <summary>Creates a controller with an initial desired connection count (clamped to at least 1).</summary>
    public ConnectionController(int desiredConnections)
    {
        _desired = Math.Max(1, desiredConnections);
    }

    /// <summary>Raised whenever the desired or active count changes. Handlers must not block.</summary>
    public event EventHandler<ConnectionCountChangedEventArgs>? Changed;

    /// <summary>Raised when <see cref="DesiredConnections"/> changes, so the engine reconciles. Internal hook.</summary>
    internal event Action? DesiredChanged;

    /// <summary>The current target number of connections.</summary>
    public int DesiredConnections
    {
        get
        {
            lock (_gate)
            {
                return _desired;
            }
        }
    }

    /// <summary>The number of connections currently transferring (written by the engine).</summary>
    public int ActiveConnections
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>
    /// Sets the desired connection count (clamped to at least 1) and triggers reconciliation. Takes effect
    /// without restarting the download (AC0): higher spawns more connections, lower drains at boundaries.
    /// </summary>
    public void SetDesiredConnections(int count)
    {
        int clamped = Math.Max(1, count);
        bool changed;
        lock (_gate)
        {
            changed = _desired != clamped;
            _desired = clamped;
        }

        if (changed)
        {
            DesiredChanged?.Invoke();
            RaiseChanged();
        }
    }

    /// <summary>Engine-side: records the live active count and notifies observers.</summary>
    internal void ReportActiveConnections(int count)
    {
        bool changed;
        lock (_gate)
        {
            changed = _active != count;
            _active = count;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        EventHandler<ConnectionCountChangedEventArgs>? handler = Changed;
        if (handler is null)
        {
            return;
        }

        int desired;
        int active;
        lock (_gate)
        {
            desired = _desired;
            active = _active;
        }

        handler(this, new ConnectionCountChangedEventArgs(desired, active));
    }
}
