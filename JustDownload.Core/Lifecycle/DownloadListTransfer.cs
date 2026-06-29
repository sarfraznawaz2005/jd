using JustDownload.Core.Data.Models;
using JustDownload.Core.Data.Repositories;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Imports a URL list and exports the queue as a reusable download list (TASK-140). Export reads the current
/// downloads and writes M3U/CSV/JSON (format from the file extension); import parses the URLs out of such a
/// file and enqueues them through the shared <see cref="IBatchEnqueuer"/> (range expansion, scheme validation,
/// file-name derivation), so imported lists go through exactly the same path as pasted URLs.
/// </summary>
public interface IDownloadListTransfer
{
    /// <summary>Writes the current queue to <paramref name="filePath"/>; returns the number of entries written.</summary>
    Task<int> ExportAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports the URLs from <paramref name="filePath"/> into the queue, saving each into
    /// <paramref name="destinationDirectory"/>; returns the created download ids.
    /// </summary>
    Task<IReadOnlyList<long>> ImportAsync(
        string filePath, string destinationDirectory, CancellationToken cancellationToken = default);
}

internal sealed class DownloadListTransfer : IDownloadListTransfer
{
    private readonly IDownloadRepository _repository;
    private readonly IBatchEnqueuer _batchEnqueuer;

    public DownloadListTransfer(IDownloadRepository repository, IBatchEnqueuer batchEnqueuer)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(batchEnqueuer);
        _repository = repository;
        _batchEnqueuer = batchEnqueuer;
    }

    public async Task<int> ExportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        IReadOnlyList<Download> downloads = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var entries = downloads
            .Select(d => new DownloadListEntry { Url = d.Url, FileName = d.Filename })
            .ToList();

        string content = DownloadListSerializer.Serialize(entries, DownloadListSerializer.DetectFormat(filePath));
        await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);
        return entries.Count;
    }

    public async Task<IReadOnlyList<long>> ImportAsync(
        string filePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> urls =
            DownloadListSerializer.ParseUrls(content, DownloadListSerializer.DetectFormat(filePath));
        if (urls.Count == 0)
        {
            return [];
        }

        var request = new BatchEnqueueRequest
        {
            Text = string.Join('\n', urls),
            DestinationDirectory = destinationDirectory,
        };
        return await _batchEnqueuer.EnqueueAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
