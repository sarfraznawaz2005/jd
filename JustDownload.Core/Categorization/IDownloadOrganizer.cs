namespace JustDownload.Core.Categorization;

/// <summary>
/// Moves a completed download into a per-category subfolder when the user has enabled it (TASK-046,
/// US-8 AC3). A no-op (returns the original path) when the toggle is off, so callers can always invoke
/// it after a download finishes.
/// </summary>
public interface IDownloadOrganizer
{
    /// <summary>
    /// Organizes <paramref name="completedFilePath"/> by <paramref name="category"/> if enabled, and
    /// returns the file's final path (unchanged when organizing is off or the file is already in place).
    /// </summary>
    /// <param name="completedFilePath">The path of the just-completed download.</param>
    /// <param name="category">The file's resolved category.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<string> OrganizeAsync(
        string completedFilePath,
        FileCategory category,
        CancellationToken cancellationToken = default);
}
