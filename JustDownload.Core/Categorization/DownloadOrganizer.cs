using JustDownload.Core.Settings;

namespace JustDownload.Core.Categorization;

/// <summary>
/// Default <see cref="IDownloadOrganizer"/> (TASK-046). Reads the <see cref="ISettingsService"/> toggle
/// and root directory, resolves the category's folder via the editable <see cref="CategoryFolderRules"/>,
/// and moves the completed file there — deduplicating the name on collision so an existing file is never
/// overwritten. When the toggle is off it returns the original path untouched.
/// </summary>
internal sealed class DownloadOrganizer : IDownloadOrganizer
{
    private readonly ISettingsService _settings;
    private readonly CategoryFolderRules _folders;

    public DownloadOrganizer(ISettingsService settings, CategoryFolderRules folders)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(folders);
        _settings = settings;
        _folders = folders;
    }

    public Task<string> OrganizeAsync(
        string completedFilePath,
        FileCategory category,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(completedFilePath);
        cancellationToken.ThrowIfCancellationRequested();

        AppSettings settings = _settings.Current;
        if (!settings.OrganizeByCategory)
        {
            return Task.FromResult(completedFilePath); // AC0: toggle off → leave the file where it is.
        }

        string source = Path.GetFullPath(completedFilePath);
        string root = string.IsNullOrWhiteSpace(settings.OrganizedRootDirectory)
            ? Path.GetDirectoryName(source) ?? source
            : Path.GetFullPath(settings.OrganizedRootDirectory);

        string targetDirectory = Path.Combine(root, _folders.GetFolderName(category));
        string target = Path.Combine(targetDirectory, Path.GetFileName(source));

        if (string.Equals(source, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(source); // Already organized into this folder.
        }

        Directory.CreateDirectory(targetDirectory);
        target = MakeUnique(target);
        File.Move(source, target);
        return Task.FromResult(target);
    }

    /// <summary>Appends " (n)" before the extension until the path does not exist.</summary>
    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(
                directory, $"{name} ({n.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
