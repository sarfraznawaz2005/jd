namespace JustDownload.App.ViewModels;

/// <summary>
/// Pure aggregation of the active downloads' live figures for the status bar (TASK-049 AC2): how many are
/// active, their combined speed, and their total connections. Membership is driven by status changes; speed
/// and connection counts by progress updates. Side-effect-free and unit-testable; the view-model wraps it
/// with thread-marshalling and change notification.
/// </summary>
public sealed class StatusAggregate
{
    private readonly Dictionary<long, Entry> _active = [];

    /// <summary>Marks a download active (idempotent); its figures fill in as progress arrives.</summary>
    public void Activate(long id)
    {
        if (!_active.ContainsKey(id))
        {
            _active[id] = default;
        }
    }

    /// <summary>Removes a download from the active set (it paused, finished, failed, or expired).</summary>
    public void Deactivate(long id) => _active.Remove(id);

    /// <summary>Updates an active download's live speed and connection count from a progress snapshot.</summary>
    public void Update(long id, double bytesPerSecond, int connections)
    {
        _active[id] = new Entry(Math.Max(0, bytesPerSecond), Math.Max(0, connections));
    }

    /// <summary>The number of currently active downloads.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>The combined transfer rate of the active downloads, in bytes/second.</summary>
    public double TotalBytesPerSecond
    {
        get
        {
            double total = 0;
            foreach (Entry entry in _active.Values)
            {
                total += entry.BytesPerSecond;
            }

            return total;
        }
    }

    /// <summary>The combined connection count across the active downloads.</summary>
    public int TotalConnections
    {
        get
        {
            int total = 0;
            foreach (Entry entry in _active.Values)
            {
                total += entry.Connections;
            }

            return total;
        }
    }

    private readonly record struct Entry(double BytesPerSecond, int Connections);
}
