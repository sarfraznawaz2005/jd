namespace JustDownload.Core.Lifecycle;

/// <summary>
/// An immutable snapshot of a download's live progress (TASK-031, feeds US-15b's per-download detail view):
/// current status, bytes done vs. total, instantaneous speed, the estimated time remaining, and whether the
/// transfer can be paused and resumed. Built through <see cref="Create"/> so the derived fields (percent and
/// ETA) are computed once, deterministically, from the inputs — the computation is pure and unit-tested.
/// </summary>
public sealed record DownloadProgress
{
    /// <summary>The lifecycle status at the moment of the snapshot.</summary>
    public required DownloadStatus Status { get; init; }

    /// <summary>Bytes written so far.</summary>
    public required long DownloadedBytes { get; init; }

    /// <summary>Total size in bytes when known; <see langword="null"/> for unknown-length sources.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Instantaneous transfer rate in bytes per second (0 when idle or just started).</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>
    /// Completed fraction in <c>[0, 1]</c>, or <see langword="null"/> when the total size is unknown so a
    /// percentage cannot be derived.
    /// </summary>
    public double? Fraction { get; init; }

    /// <summary>
    /// Estimated time until completion, or <see langword="null"/> when it cannot be estimated (unknown total
    /// or zero speed).
    /// </summary>
    public TimeSpan? Eta { get; init; }

    /// <summary>
    /// Whether the download can be paused and later resumed from its current offset (the server advertised
    /// range support / a known size). A non-resumable transfer must restart from zero.
    /// </summary>
    public bool Resumable { get; init; }

    /// <summary>
    /// Builds a snapshot, deriving <see cref="Fraction"/> and <see cref="Eta"/> from the raw inputs. This is
    /// a pure function: the same inputs always yield the same snapshot.
    /// </summary>
    /// <param name="status">Current lifecycle status.</param>
    /// <param name="downloadedBytes">Bytes written so far (clamped at 0).</param>
    /// <param name="totalBytes">Total size when known.</param>
    /// <param name="bytesPerSecond">Current speed; values ≤ 0 mean "unknown / idle".</param>
    /// <param name="resumable">Whether the transfer can resume from its current offset.</param>
    public static DownloadProgress Create(
        DownloadStatus status,
        long downloadedBytes,
        long? totalBytes,
        double bytesPerSecond,
        bool resumable)
    {
        long done = Math.Max(0, downloadedBytes);
        double speed = bytesPerSecond > 0 ? bytesPerSecond : 0;

        double? fraction = null;
        TimeSpan? eta = null;
        if (totalBytes is > 0)
        {
            long total = totalBytes.Value;
            long capped = Math.Min(done, total);
            fraction = (double)capped / total;

            if (status == DownloadStatus.Completed)
            {
                eta = TimeSpan.Zero;
            }
            else if (speed > 0)
            {
                double remainingSeconds = (total - capped) / speed;
                // Guard against overflow when speed is a tiny trickle.
                eta = remainingSeconds < TimeSpan.MaxValue.TotalSeconds
                    ? TimeSpan.FromSeconds(remainingSeconds)
                    : null;
            }
        }

        return new DownloadProgress
        {
            Status = status,
            DownloadedBytes = done,
            TotalBytes = totalBytes,
            BytesPerSecond = speed,
            Fraction = fraction,
            Eta = eta,
            Resumable = resumable,
        };
    }
}
