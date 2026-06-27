namespace JustDownload.Core.Categorization;

/// <summary>
/// The user-editable mapping from a <see cref="FileCategory"/> to the subfolder name used when
/// organizing completed downloads by category (TASK-046, US-8 AC3). Seeded with sensible defaults via
/// <see cref="CreateDefault"/>; callers can rename any category's folder. Names are sanitized to a safe
/// single path segment so they can never escape the organize root.
/// </summary>
public sealed class CategoryFolderRules
{
    private readonly Dictionary<FileCategory, string> _folders = [];

    /// <summary>Sets (or overrides) the folder name for a category. Returns this instance for chaining.</summary>
    public CategoryFolderRules SetFolderName(FileCategory category, string folderName)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown file category.");
        }

        string safe = Sanitize(folderName)
            ?? throw new ArgumentException("Folder name must be a non-empty, safe path segment.", nameof(folderName));
        _folders[category] = safe;
        return this;
    }

    /// <summary>Gets the folder name for a category, falling back to the category's own name.</summary>
    public string GetFolderName(FileCategory category) =>
        _folders.TryGetValue(category, out string? name) ? name : category.ToString();

    /// <summary>Builds the rule set with the product's default folder names.</summary>
    public static CategoryFolderRules CreateDefault() =>
        new CategoryFolderRules()
            .SetFolderName(FileCategory.Video, "Video")
            .SetFolderName(FileCategory.Audio, "Audio")
            .SetFolderName(FileCategory.Document, "Documents")
            .SetFolderName(FileCategory.Compressed, "Compressed")
            .SetFolderName(FileCategory.Program, "Programs")
            .SetFolderName(FileCategory.Image, "Images")
            .SetFolderName(FileCategory.Other, "Other");

    /// <summary>Reduces a name to a safe single path segment, or <see langword="null"/> if nothing usable remains.</summary>
    private static string? Sanitize(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(folderName.Length);
        foreach (char ch in folderName)
        {
            // Strip path separators and any character invalid in a file/dir name.
            bool invalid = ch is '/' or '\\' || Array.IndexOf(Path.GetInvalidFileNameChars(), ch) >= 0;
            builder.Append(invalid ? '_' : ch);
        }

        string cleaned = builder.ToString().Trim().TrimEnd('.', ' ').Trim();
        return cleaned is "" or "." or ".." ? null : cleaned;
    }
}
