using JustDownload.Core.Abstractions;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;

namespace JustDownload.Core.Storage;

/// <summary>
/// Default <see cref="ISegmentCheckpointer"/> (TASK-025). Coalesces the latest state of each segment in
/// a dictionary keyed by segment id and flushes them to <see cref="ISegmentRepository"/> on demand. The
/// flush interval is measured with the injected <see cref="IClock"/> so the throttle is deterministic
/// and unit-testable. Each segment is persisted via a single <c>UPDATE</c>, which SQLite applies
/// atomically.
/// </summary>
internal sealed class SegmentCheckpointer : ISegmentCheckpointer
{
    /// <summary>Default minimum time between database flushes (keeps checkpoints off the per-chunk path).</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly ISegmentRepository _segments;
    private readonly IClock _clock;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly Dictionary<long, DownloadSegment> _pending = [];
    private DateTimeOffset _lastFlush;

    public SegmentCheckpointer(ISegmentRepository segments, IClock clock)
        : this(segments, clock, DefaultInterval)
    {
    }

    internal SegmentCheckpointer(ISegmentRepository segments, IClock clock, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(clock);
        _segments = segments;
        _clock = clock;
        _interval = interval;
        _lastFlush = clock.UtcNow;
    }

    public void Record(DownloadSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Id <= 0)
        {
            throw new ArgumentException("Segment must be persisted (Id > 0) before checkpointing.", nameof(segment));
        }

        lock (_gate)
        {
            _pending[segment.Id] = segment;
        }
    }

    public bool IsFlushDue
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0 && _clock.UtcNow - _lastFlush >= _interval;
            }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        DownloadSegment[] snapshot;
        lock (_gate)
        {
            _lastFlush = _clock.UtcNow;
            if (_pending.Count == 0)
            {
                return;
            }

            snapshot = new DownloadSegment[_pending.Count];
            _pending.Values.CopyTo(snapshot, 0);
            _pending.Clear();
        }

        foreach (DownloadSegment segment in snapshot)
        {
            await _segments.UpdateAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }
}
