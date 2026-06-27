using JustDownload.Core.Categorization;

namespace JustDownload.App.ViewModels;

/// <summary>The kind of filter a sidebar node applies to the downloads list (TASK-050).</summary>
public enum DownloadFilterKind
{
    /// <summary>Everything — the "All Downloads" root.</summary>
    All,

    /// <summary>A single file-type category.</summary>
    Category,

    /// <summary>Anything not finished (queued / active / paused / failed / expired).</summary>
    Incomplete,

    /// <summary>Finished downloads only.</summary>
    Completed,
}

/// <summary>
/// A predicate over a download row used to filter the list when a sidebar node is selected (TASK-050).
/// Pure and value-equatable so the sidebar can compare and re-apply filters cheaply.
/// </summary>
public sealed record DownloadFilter(DownloadFilterKind Kind, FileCategory? Category = null)
{
    /// <summary>The "All Downloads" filter (matches everything).</summary>
    public static DownloadFilter All { get; } = new(DownloadFilterKind.All);

    /// <summary>Whether <paramref name="row"/> belongs in the list under this filter.</summary>
    public bool Matches(DownloadRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Kind switch
        {
            DownloadFilterKind.All => true,
            DownloadFilterKind.Category => row.Category == Category,
            DownloadFilterKind.Incomplete => !row.IsCompleted,
            DownloadFilterKind.Completed => row.IsCompleted,
            _ => true,
        };
    }
}
