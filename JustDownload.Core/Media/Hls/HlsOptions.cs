namespace JustDownload.Core.Media.Hls;

/// <summary>Tuning for HLS segment downloading (TASK-037).</summary>
public sealed class HlsOptions
{
    /// <summary>
    /// The maximum number of segments fetched concurrently (AC1). Kept modest so a many-segment playlist
    /// does not open an unbounded number of connections; the shared HTTP handler still pools them.
    /// </summary>
    public int MaxParallelSegments { get; set; } = 6;
}
