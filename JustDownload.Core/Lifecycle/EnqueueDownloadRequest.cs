namespace JustDownload.Core.Lifecycle;

/// <summary>
/// The information needed to add a new download to the queue (TASK-031). The destination directory and file
/// name are resolved by the caller (the App resolves a server-suggested name via the transport before
/// enqueuing), so the manager can form a concrete path and the record is persistable up front.
/// </summary>
public sealed record EnqueueDownloadRequest
{
    /// <summary>The source URL.</summary>
    public required Uri Url { get; init; }

    /// <summary>The directory the file is written to.</summary>
    public required string DestinationDirectory { get; init; }

    /// <summary>The target file name within <see cref="DestinationDirectory"/>.</summary>
    public required string FileName { get; init; }

    /// <summary>Total size in bytes when already known (e.g. from a prior probe); otherwise <see langword="null"/>.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The referring page URL, sent as <c>Referer</c> and used by renew flows.</summary>
    public string? Referrer { get; init; }

    /// <summary>The file-type category code (Video, Document, …) for auto-organization.</summary>
    public string? CategoryType { get; init; }

    /// <summary>Per-download maximum connection count; <see langword="null"/> uses the engine default.</summary>
    public int? MaxConnections { get; init; }

    /// <summary>Per-download speed cap in bytes/second; <see langword="null"/>/<c>0</c> means unlimited.</summary>
    public long? SpeedLimit { get; init; }
}
