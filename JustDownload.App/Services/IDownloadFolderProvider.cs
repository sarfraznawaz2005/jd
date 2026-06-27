using JustDownload.Core.Categorization;

namespace JustDownload.App.Services;

/// <summary>
/// Resolves the default save location for a new download (TASK-053): the base downloads directory and the
/// per-category subfolder under it. Abstracted so the New URL dialog's auto-fill is testable without touching
/// the real filesystem or environment.
/// </summary>
public interface IDownloadFolderProvider
{
    /// <summary>The base directory new downloads default into (e.g. the OS "Downloads" folder).</summary>
    string GetBaseFolder();

    /// <summary>The default folder for <paramref name="category"/> — the base folder plus its category subfolder.</summary>
    string GetFolderForCategory(FileCategory category);
}
