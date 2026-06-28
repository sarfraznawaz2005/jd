using System.Collections.Concurrent;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;
using JustDownload.Core.Lifecycle;
using JustDownload.Core.Security;
using Microsoft.Extensions.Logging;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="IDownloadActions"/>: drives the <see cref="IDownloadManager"/> and owns a per-download
/// <see cref="CancellationTokenSource"/> for each in-flight transfer so that a "pause" is a clean cancel of
/// exactly that download (the engine then checkpoints and transitions to Paused). The dictionary is the only
/// session state — once a run finishes the entry is disposed and removed, so paused/cancelled downloads hold
/// no sockets or handles (§5 "pause/cancel is instant; no orphaned sockets").
/// </summary>
public sealed partial class DownloadActionsService : IDownloadActions, IDisposable
{
    private readonly IDownloadManager _manager;
    private readonly IDownloadRepository _repository;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<DownloadActionsService> _logger;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _running = new();

    public DownloadActionsService(
        IDownloadManager manager,
        IDownloadRepository repository,
        ISecretStore secretStore,
        ILogger<DownloadActionsService> logger)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentNullException.ThrowIfNull(logger);
        _manager = manager;
        _repository = repository;
        _secretStore = secretStore;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Start(long id)
    {
        var cts = new CancellationTokenSource();
        if (!_running.TryAdd(id, cts))
        {
            // Already running — discard the spare source and leave the active run untouched.
            cts.Dispose();
            return;
        }

        _ = RunAsync(id, cts);
    }

    private async Task RunAsync(long id, CancellationTokenSource cts)
    {
        try
        {
            await _manager.StartAsync(id, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The manager already transitions the download to Failed and raises events; this catch only keeps
            // a fire-and-forget run from becoming an unobserved task exception. Surfaced for diagnostics.
            DownloadRunFaulted(id, ex);
        }
        finally
        {
            if (_running.TryRemove(id, out CancellationTokenSource? removed))
            {
                removed.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public void Pause(long id)
    {
        if (_running.TryGetValue(id, out CancellationTokenSource? cts))
        {
            cts.Cancel();
        }
    }

    /// <inheritdoc />
    public bool IsRunning(long id) => _running.ContainsKey(id);

    /// <inheritdoc />
    public async Task RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        Pause(id);

        // Remove any keychain-stored cookies for this download before its row goes, so no secret is orphaned
        // in the OS vault (§5, TASK-091).
        Download? record = await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (record?.CookieSecretRef is { Length: > 0 } cookieRef)
        {
            await _secretStore.DeleteAsync(cookieRef, cancellationToken).ConfigureAwait(false);
        }

        await _repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Cancel every in-flight transfer so quitting leaves nothing running (§5).
        foreach (KeyValuePair<long, CancellationTokenSource> entry in _running)
        {
            entry.Value.Cancel();
            entry.Value.Dispose();
        }

        _running.Clear();
    }

    private void DownloadRunFaulted(long id, Exception ex) => LogRunFaulted(_logger, id, ex);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Background download run for {DownloadId} faulted.")]
    private static partial void LogRunFaulted(ILogger logger, long downloadId, Exception exception);
}
