namespace JustDownload.Core.Lifecycle;

/// <summary>
/// A point-in-time, UI-facing snapshot of one connection of an active download (TASK-054, US-15c). The
/// manager folds the engine's raw <see cref="Downloading.ConnectionProgress"/> reports into these, adding the
/// derived per-connection <see cref="BytesPerSecond"/> the engine itself does not compute. Surfaced through
/// <see cref="IDownloadManager.GetConnections"/> for the per-download detail view's Connections tab.
/// </summary>
public sealed record ConnectionStat
{
    /// <summary>The stable connection/worker id (0-based).</summary>
    public required int ConnectionId { get; init; }

    /// <summary>The index of the segment this connection is currently working.</summary>
    public required int SegmentIndex { get; init; }

    /// <summary>The first byte of the connection's current segment.</summary>
    public required long Start { get; init; }

    /// <summary>The inclusive last byte of the connection's current segment.</summary>
    public required long End { get; init; }

    /// <summary>Bytes written so far for the current segment.</summary>
    public required long DownloadedBytes { get; init; }

    /// <summary>Total bytes in the current segment.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>The connection's instantaneous transfer rate in bytes per second.</summary>
    public required double BytesPerSecond { get; init; }

    /// <summary>Whether the connection is still transferring (<see langword="false"/> once it has finished).</summary>
    public required bool IsActive { get; init; }

    /// <summary>Completed fraction of the current segment in <c>[0, 1]</c> (0 when the segment is empty).</summary>
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;
}
