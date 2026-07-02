namespace JustDownload.Core.Media.Dash;

/// <summary>Tuning for DASH segment downloading (TASK-102).</summary>
public sealed class DashOptions
{
    /// <summary>
    /// The maximum number of segments fetched concurrently. Kept modest so a many-segment representation
    /// does not open an unbounded number of connections; the shared HTTP handler still pools them.
    /// </summary>
    public int MaxParallelSegments { get; set; } = 6;
}
