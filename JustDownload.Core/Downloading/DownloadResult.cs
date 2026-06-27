namespace JustDownload.Core.Downloading;

/// <summary>
/// The outcome of a completed download (TASK-026): bytes written, the resolved URL/file name, and
/// segmentation telemetry — whether the single-connection fallback was used, how many initial segments
/// ran, and how many work-steals occurred (the dynamic part of dynamic segmentation).
/// </summary>
public sealed record DownloadResult
{
    /// <summary>Total bytes written to the destination file.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>The resource URL after redirects.</summary>
    public required Uri FinalUri { get; init; }

    /// <summary>The server-suggested file name.</summary>
    public required string FileName { get; init; }

    /// <summary>Whether the download ran on a single connection (range-less or unknown-size resource).</summary>
    public required bool SingleConnection { get; init; }

    /// <summary>The number of segments the resource was initially split into.</summary>
    public required int InitialSegments { get; init; }

    /// <summary>How many times an idle connection re-split (stole) a remaining range.</summary>
    public required int Steals { get; init; }
}
