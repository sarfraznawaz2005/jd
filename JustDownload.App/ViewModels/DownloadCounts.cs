using JustDownload.Core.Categorization;

namespace JustDownload.App.ViewModels;

/// <summary>
/// A snapshot of the download counts the sidebar badges display (TASK-050): the total, per-category, and the
/// status groups. Computed from the full row set whenever it changes, then mapped onto the sidebar nodes.
/// </summary>
public sealed record DownloadCounts(
    int All,
    IReadOnlyDictionary<FileCategory, int> ByCategory,
    int Incomplete,
    int Completed)
{
    /// <summary>An all-zero snapshot.</summary>
    public static DownloadCounts Empty { get; } = new(0, new Dictionary<FileCategory, int>(), 0, 0);

    /// <summary>The count for a single category (0 when none).</summary>
    public int ForCategory(FileCategory category) => ByCategory.TryGetValue(category, out int n) ? n : 0;
}
