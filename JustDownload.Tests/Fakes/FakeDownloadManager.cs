using JustDownload.Core.Downloading;
using JustDownload.Core.Lifecycle;

namespace JustDownload.Tests.Fakes;

/// <summary>
/// A hand-rolled <see cref="IDownloadManager"/> stand-in for the services that only need to observe its
/// events (auto-extract, organizer, post-download command, notifications, scheduler) or to intercept one
/// operation (batch enqueue, the queue's start gating).
/// <para>
/// Every operation throws <see cref="NotSupportedException"/> by default, so a test that reaches an
/// operation it did not intend to exercise fails loudly instead of silently getting a null. Override only
/// the member under test.
/// </para>
/// This replaces what were seven near-identical private fakes — four of them byte-for-byte copies — each of
/// which had to be edited by hand every time <see cref="IDownloadManager"/> gained a member.
/// </summary>
public class FakeDownloadManager : IDownloadManager
{
    public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<DownloadProgressChangedEventArgs>? ProgressChanged;

    /// <summary>Fires <see cref="StatusChanged"/> as if a download had just moved to <paramref name="current"/>.</summary>
    public void Raise(long id, DownloadStatus current, DownloadStatus? previous = DownloadStatus.Active) =>
        StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs(id, previous, current));

    /// <summary>Fires <see cref="ProgressChanged"/> with the given snapshot.</summary>
    public void RaiseProgress(long id, DownloadProgress progress) =>
        ProgressChanged?.Invoke(this, new DownloadProgressChangedEventArgs(id, progress));

    public virtual Task<long> EnqueueAsync(
        EnqueueDownloadRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<DownloadResult> StartAsync(long id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<DownloadResult> RenewAsync(
        long id, Uri newUrl, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<DownloadResult> RestartAsync(long id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual DownloadProgress? GetProgress(long id) => null;

    public virtual IReadOnlyList<ConnectionStat> GetConnections(long id) => [];

    /// <summary>Ids passed to <see cref="Forget"/>, so tests can assert the cleanup happened.</summary>
    public List<long> Forgotten { get; } = [];

    public virtual void Forget(long id) => Forgotten.Add(id);
}
