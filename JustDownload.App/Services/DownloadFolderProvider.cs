using JustDownload.Core.Categorization;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="IDownloadFolderProvider"/>: the base is the OS user profile's <c>Downloads</c> folder,
/// and each category maps to a subfolder under it using the shared <see cref="CategoryFolderRules"/> (so the
/// dialog's "Save to" matches the auto-organization layout, e.g. <c>…/Downloads/Programs</c>).
/// </summary>
public sealed class DownloadFolderProvider : IDownloadFolderProvider
{
    private readonly CategoryFolderRules _folderRules;

    public DownloadFolderProvider(CategoryFolderRules folderRules)
    {
        ArgumentNullException.ThrowIfNull(folderRules);
        _folderRules = folderRules;
    }

    /// <inheritdoc />
    public string GetBaseFolder()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? "Downloads" : Path.Combine(profile, "Downloads");
    }

    /// <inheritdoc />
    public string GetFolderForCategory(FileCategory category) =>
        Path.Combine(GetBaseFolder(), _folderRules.GetFolderName(category));
}
