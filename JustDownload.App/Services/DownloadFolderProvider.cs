using JustDownload.Core.Categorization;
using JustDownload.Core.Settings;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="IDownloadFolderProvider"/>: the base is the user's configured default download folder
/// (<see cref="AppSettings.DefaultDownloadDirectory"/>, TASK-121) when set, otherwise the OS user profile's
/// <c>Downloads</c> folder; each category maps to a subfolder under it using the shared
/// <see cref="CategoryFolderRules"/> (so the dialog's "Save to" matches the auto-organization layout, e.g.
/// <c>…/Downloads/Programs</c>).
/// </summary>
public sealed class DownloadFolderProvider : IDownloadFolderProvider
{
    private readonly CategoryFolderRules _folderRules;
    private readonly ISettingsService _settings;

    public DownloadFolderProvider(CategoryFolderRules folderRules, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(folderRules);
        ArgumentNullException.ThrowIfNull(settings);
        _folderRules = folderRules;
        _settings = settings;
    }

    /// <inheritdoc />
    public string GetBaseFolder()
    {
        string? configured = _settings.Current.DefaultDownloadDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? "Downloads" : Path.Combine(profile, "Downloads");
    }

    /// <inheritdoc />
    public string GetFolderForCategory(FileCategory category) =>
        Path.Combine(GetBaseFolder(), _folderRules.GetFolderName(category));
}
