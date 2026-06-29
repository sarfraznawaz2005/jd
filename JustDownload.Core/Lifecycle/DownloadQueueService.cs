using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Settings;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Default <see cref="IDownloadQueueService"/> (TASK-072). Pumps queued downloads into the manager up to the
/// <c>MaxConcurrentDownloads</c> limit, ordered by priority. A single-flight pump (guarded by a semaphore)
/// queries the queued rows ordered by priority and starts as many as there are free slots; each running
/// download holds its own linked <see cref="CancellationTokenSource"/> so the queue can pause it. When a
/// run ends — completion, failure, or pause — its slot is freed and the pump runs again. New enqueues are
/// observed via <see cref="IDownloadManager.StatusChanged"/> and pumped automatically once the queue is
/// started. The concurrency limit is re-read from settings on every pump, so changing it takes effect live.
/// </summary>
internal sealed partial class DownloadQueueService : IDownloadQueueService, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, RunSlot> _running = [];
    private readonly SemaphoreSlim _pumpLock = new(1, 1);
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly ISettingsService _settings;
    private readonly ILogger<DownloadQueueService> _logger;
    private volatile bool _enabled;
    private bool _disposed;

    public DownloadQueueService(
        IDownloadManager manager,
        IDownloadRepository repository,
        ISettingsService settings,
        ILogger<DownloadQueueService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _repository = repository;
        _settings = settings;
        _logger = logger;
        _manager.StatusChanged += OnStatusChanged;
    }

    public IReadOnlyCollection<long> RunningIds
    {
        get
        {
            lock (_gate)
            {
                return _running.Keys.ToArray();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _enabled = true;
        await PumpAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _enabled = false;

        RunSlot[] running;
        lock (_gate)
        {
            running = _running.Values.ToArray();
        }

        // Cancelling each run's token makes the manager pause it (checkpoint preserved).
        foreach (RunSlot slot in running)
        {
            slot.Cts.Cancel();
        }

        return Task.CompletedTask;
    }

    public async Task SetPriorityAsync(long id, int priority, CancellationToken cancellationToken = default)
    {
        await _repository.SetPriorityAsync(id, priority, cancellationToken).ConfigureAwait(false);
        await PumpAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReorderAsync(
        IReadOnlyList<long> orderedIdsHighestFirst, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIdsHighestFirst);

        // Strictly descending priorities (spaced so a later single insert can slot between two) — the first
        // id gets the highest, so it runs soonest. Persisted in one batched UPDATE (TASK-106).
        var priorities = new List<DownloadPriority>(orderedIdsHighestFirst.Count);
        int priority = orderedIdsHighestFirst.Count * 10;
        foreach (long id in orderedIdsHighestFirst)
        {
            priorities.Add(new DownloadPriority(id, priority));
            priority -= 10;
        }

        await _repository.SetPrioritiesAsync(priorities, cancellationToken).ConfigureAwait(false);
        await PumpAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        // A freshly queued download (including the initial enqueue) is a chance to fill a free slot.
        if (_enabled && e.Current == DownloadStatus.Queued)
        {
            _ = PumpAsync(CancellationToken.None);
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return;
        }

        await _pumpLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int slots = AvailableSlots();
            if (slots <= 0)
            {
                return;
            }

            // Per-category caps (TASK-141): a category at its cap is skipped so a lower-priority download of a
            // different (uncapped) category can take the free global slot instead of blocking the pump.
            IReadOnlyDictionary<string, int> caps =
                CategoryConcurrency.Parse(_settings.Current.CategoryConcurrencyLimits);
            Dictionary<string, int> runningByCategory = SnapshotRunningByCategory();

            IReadOnlyList<Download> queued = await _repository
                .GetByStatusOrderedByPriorityAsync(DownloadStatusCodes.Queued, cancellationToken)
                .ConfigureAwait(false);

            foreach (Download download in queued)
            {
                if (slots <= 0)
                {
                    break;
                }

                string category = download.CategoryType ?? string.Empty;
                if (caps.TryGetValue(category, out int cap)
                    && runningByCategory.GetValueOrDefault(category) >= cap)
                {
                    continue; // this category is full — try the next queued download
                }

                if (TryBeginRun(download.Id, category))
                {
                    slots--;
                    runningByCategory[category] = runningByCategory.GetValueOrDefault(category) + 1;
                }
            }
        }
        finally
        {
            _pumpLock.Release();
        }
    }

    private int AvailableSlots()
    {
        int max = Math.Max(1, _settings.Current.MaxConcurrentDownloads);
        lock (_gate)
        {
            return max - _running.Count;
        }
    }

    private Dictionary<string, int> SnapshotRunningByCategory()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (RunSlot slot in _running.Values)
            {
                counts[slot.Category] = counts.GetValueOrDefault(slot.Category) + 1;
            }
        }

        return counts;
    }

    private bool TryBeginRun(long id, string category)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running.ContainsKey(id))
            {
                return false; // already running (a concurrent pump or a still-in-flight start)
            }

            cts = new CancellationTokenSource();
            _running[id] = new RunSlot(cts, category);
        }

        _ = RunAsync(id, cts);
        return true;
    }

    private async Task RunAsync(long id, CancellationTokenSource cts)
    {
        try
        {
            await _manager.StartAsync(id, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Paused by StopAsync — the manager has checkpointed it; nothing more to do here.
        }
        catch (Exception ex)
        {
            // The manager already transitioned the download to Failed and raised the event.
            LogRunFailed(_logger, id, ex);
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(id);
                cts.Dispose();
            }

            // A slot just freed — start the next queued download.
            await PumpAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _manager.StatusChanged -= OnStatusChanged;

        lock (_gate)
        {
            foreach (RunSlot slot in _running.Values)
            {
                slot.Cts.Dispose();
            }

            _running.Clear();
        }

        _pumpLock.Dispose();
    }

    private readonly record struct RunSlot(CancellationTokenSource Cts, string Category);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Queued download {Id} failed to run.")]
    private static partial void LogRunFailed(ILogger logger, long id, Exception exception);
}
