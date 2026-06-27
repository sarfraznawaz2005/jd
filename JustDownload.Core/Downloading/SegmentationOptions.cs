namespace JustDownload.Core.Downloading;

/// <summary>
/// Tunables for dynamic segmentation (TASK-026). Defaults: 8 connections, and a 1 MiB floor on both
/// the initial segment size and a work-steal split so the engine never spins up many connections (or
/// re-splits) for trivially small remainders — keeping overhead low on slow systems.
/// </summary>
public sealed class SegmentationOptions
{
    /// <summary>The default number of connections when the caller does not specify one. Default 8.</summary>
    public int DefaultConnections { get; set; } = 8;

    /// <summary>The smallest initial segment; a resource splits into fewer segments below this. Default 1 MiB.</summary>
    public long MinSegmentSize { get; set; } = 1L * 1024 * 1024;

    /// <summary>A work-steal only happens when the largest remaining range is at least twice this. Default 1 MiB.</summary>
    public long MinStealSize { get; set; } = 1L * 1024 * 1024;
}
