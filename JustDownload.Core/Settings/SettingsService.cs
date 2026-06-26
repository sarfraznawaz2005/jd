using JustDownload.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Settings;

/// <summary>
/// Default <see cref="ISettingsService"/>: a thread-safe, in-memory-cached view over the persistent
/// settings repository (TASK-020). Mutations are serialized through a <see cref="SemaphoreSlim"/> so
/// concurrent updates can't interleave the read-modify-persist sequence, and only the keys that
/// actually changed are written (cheap, and avoids spurious churn). The <see cref="Changed"/> event
/// fires outside the lock to avoid re-entrancy deadlocks if a handler calls back in.
/// </summary>
internal sealed class SettingsService : ISettingsService, IDisposable
{
    private readonly ISettingsRepository _repository;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly object _snapshotLock = new();
    private AppSettings _current = new();

    public SettingsService(ISettingsRepository repository, ILogger<SettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public AppSettings Current
    {
        get
        {
            lock (_snapshotLock)
            {
                return _current;
            }
        }
    }

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyDictionary<string, string?> stored =
                await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
            AppSettings loaded = SettingsSerializer.FromStorage(stored, _logger);
            SetSnapshot(loaded);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> UpdateAsync(
        Func<AppSettings, AppSettings> mutate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettings previous;
        AppSettings updated;
        List<string> changedKeys;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            previous = Current;
            updated = mutate(previous);
            if (updated is null)
            {
                throw new InvalidOperationException(
                    "The settings mutation returned null; it must return a settings snapshot.");
            }

            IReadOnlyDictionary<string, string> before = SettingsSerializer.ToStorage(previous);
            IReadOnlyDictionary<string, string> after = SettingsSerializer.ToStorage(updated);

            changedKeys = new List<string>();
            foreach (KeyValuePair<string, string> entry in after)
            {
                if (!string.Equals(before[entry.Key], entry.Value, StringComparison.Ordinal))
                {
                    changedKeys.Add(entry.Key);
                }
            }

            if (changedKeys.Count == 0)
            {
                return previous;
            }

            foreach (string key in changedKeys)
            {
                await _repository.SetAsync(key, after[key], cancellationToken).ConfigureAwait(false);
            }

            SetSnapshot(updated);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, updated, changedKeys));
        return updated;
    }

    private void SetSnapshot(AppSettings settings)
    {
        lock (_snapshotLock)
        {
            _current = settings;
        }
    }

    public void Dispose() => _gate.Dispose();
}
