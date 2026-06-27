using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IDownloadStatusGroups"/> (TASK-045). Holds each known download's current
/// <see cref="DownloadStatusGroup"/> in memory, seeded from the repository via <see cref="RefreshAsync"/> and
/// kept live by subscribing to <see cref="IDownloadManager.StatusChanged"/>. The grouping rule itself lives
/// in <see cref="DownloadStatusGroups"/>; this type only maintains membership and fires <see cref="Changed"/>
/// when a download actually moves bucket (or is first seen), so the UI re-renders no more than necessary.
/// </summary>
internal sealed class DownloadStatusGroupTracker : IDownloadStatusGroups, IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly Dictionary<long, DownloadStatusGroup> _groups = [];
    private readonly object _gate = new();

    public DownloadStatusGroupTracker(IDownloadManager manager, IDownloadRepository repository)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        _manager = manager;
        _repository = repository;
        _manager.StatusChanged += OnStatusChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<long> Ids(DownloadStatusGroup group)
    {
        lock (_gate)
        {
            var ids = new List<long>();
            foreach ((long id, DownloadStatusGroup g) in _groups)
            {
                if (g == group)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    public int Count(DownloadStatusGroup group)
    {
        lock (_gate)
        {
            int count = 0;
            foreach (DownloadStatusGroup g in _groups.Values)
            {
                if (g == group)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Download> all = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _groups.Clear();
            foreach (Download d in all)
            {
                _groups[d.Id] = DownloadStatusGroups.OfCode(d.Status);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        DownloadStatusGroup group = DownloadStatusGroups.Of(e.Current);
        bool changed;
        lock (_gate)
        {
            changed = !_groups.TryGetValue(e.DownloadId, out DownloadStatusGroup existing) || existing != group;
            _groups[e.DownloadId] = group;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _manager.StatusChanged -= OnStatusChanged;
}
