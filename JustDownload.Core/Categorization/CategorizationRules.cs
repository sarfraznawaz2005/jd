namespace JustDownload.Core.Categorization;

/// <summary>
/// The user-editable rule set that maps file extensions and MIME types to a <see cref="FileCategory"/>
/// (PRD US-8 AC2, "rules user-editable"). Created pre-seeded with comprehensive defaults via
/// <see cref="CreateDefault"/>; callers can then add or override individual mappings so the engine's
/// categorisation can be tuned without code changes.
/// <para>
/// Lookups are case-insensitive and tolerant of the usual noise — a leading dot on an extension and a
/// trailing <c>; charset=…</c> parameter on a MIME type are normalised away — so the same key added one
/// way resolves regardless of how it is later queried. The class is a plain data holder: the resolution
/// precedence between extension and MIME lives in <see cref="FileCategorizer"/>.
/// </para>
/// </summary>
public sealed class CategorizationRules
{
    private readonly Dictionary<string, FileCategory> _extensionMap =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, FileCategory> _mimeTypeMap =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, FileCategory> _mimeTopLevelMap =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates an empty rule set with no mappings. Every lookup returns <see langword="false"/> until
    /// mappings are added. Most callers want <see cref="CreateDefault"/> instead.
    /// </summary>
    public CategorizationRules()
    {
    }

    /// <summary>
    /// Maps a file extension (with or without a leading dot, any case) to a category. Adding a key that
    /// already exists overrides it, which is exactly how a user customises the defaults.
    /// </summary>
    /// <param name="extension">A file extension such as <c>"mp4"</c> or <c>".mp4"</c>.</param>
    /// <param name="category">The category the extension resolves to.</param>
    /// <returns>The same instance, so seeding can be chained.</returns>
    public CategorizationRules MapExtension(string extension, FileCategory category)
    {
        string key = NormalizeExtension(extension)
            ?? throw new ArgumentException("Extension must be non-empty.", nameof(extension));
        ValidateCategory(category);
        _extensionMap[key] = category;
        return this;
    }

    /// <summary>
    /// Maps a full MIME type (e.g. <c>"application/pdf"</c>) to a category. Any parameters after a
    /// <c>;</c> are ignored. Re-adding an existing type overrides it.
    /// </summary>
    /// <param name="mimeType">A MIME type such as <c>"application/pdf"</c>.</param>
    /// <param name="category">The category the MIME type resolves to.</param>
    /// <returns>The same instance, so seeding can be chained.</returns>
    public CategorizationRules MapMimeType(string mimeType, FileCategory category)
    {
        string key = NormalizeMimeType(mimeType)
            ?? throw new ArgumentException("MIME type must be non-empty.", nameof(mimeType));
        ValidateCategory(category);
        _mimeTypeMap[key] = category;
        return this;
    }

    /// <summary>
    /// Maps a MIME top-level type (e.g. <c>"video"</c>, <c>"audio"</c>, <c>"image"</c>) to a category,
    /// used as a fallback when no exact MIME mapping matches. This is what makes any <c>video/*</c>
    /// resolve to <see cref="FileCategory.Video"/> without enumerating every codec container.
    /// </summary>
    /// <param name="topLevelType">A MIME top-level type such as <c>"video"</c>.</param>
    /// <param name="category">The category the top-level type resolves to.</param>
    /// <returns>The same instance, so seeding can be chained.</returns>
    public CategorizationRules MapMimeTopLevelType(string topLevelType, FileCategory category)
    {
        ArgumentNullException.ThrowIfNull(topLevelType);
        string key = topLevelType.Trim();
        if (key.Length == 0)
        {
            throw new ArgumentException("Top-level type must be non-empty.", nameof(topLevelType));
        }

        ValidateCategory(category);
        _mimeTopLevelMap[key] = category;
        return this;
    }

    /// <summary>
    /// Resolves a category from a file extension. The input may carry a leading dot and any case.
    /// </summary>
    /// <param name="extension">A file extension such as <c>"mp4"</c> or <c>".MP4"</c>.</param>
    /// <param name="category">The resolved category when a mapping exists.</param>
    /// <returns><see langword="true"/> when a mapping exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetByExtension(string? extension, out FileCategory category)
    {
        string? key = NormalizeExtension(extension);
        if (key is not null && _extensionMap.TryGetValue(key, out category))
        {
            return true;
        }

        category = FileCategory.Other;
        return false;
    }

    /// <summary>
    /// Resolves a category from a MIME type, trying an exact match first and then the top-level type
    /// (so <c>video/x-matroska</c> still resolves via the <c>video</c> rule). Parameters after a
    /// <c>;</c> are ignored.
    /// </summary>
    /// <param name="mimeType">A MIME type such as <c>"video/mp4"</c> or <c>"application/pdf"</c>.</param>
    /// <param name="category">The resolved category when a mapping exists.</param>
    /// <returns><see langword="true"/> when a mapping exists; otherwise <see langword="false"/>.</returns>
    public bool TryGetByMimeType(string? mimeType, out FileCategory category)
    {
        string? key = NormalizeMimeType(mimeType);
        if (key is null)
        {
            category = FileCategory.Other;
            return false;
        }

        if (_mimeTypeMap.TryGetValue(key, out category))
        {
            return true;
        }

        int slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            string topLevel = key[..slash];
            if (_mimeTopLevelMap.TryGetValue(topLevel, out category))
            {
                return true;
            }
        }

        category = FileCategory.Other;
        return false;
    }

    /// <summary>
    /// Builds a rule set seeded with the product's comprehensive default mappings covering every PRD
    /// category. The returned instance is fully editable — add or override mappings to customise it.
    /// </summary>
    /// <returns>A new, pre-seeded <see cref="CategorizationRules"/>.</returns>
    public static CategorizationRules CreateDefault()
    {
        var rules = new CategorizationRules();

        rules.SeedExtensions(
            FileCategory.Video,
            "mp4", "m4v", "mkv", "webm", "mov", "avi", "wmv", "flv", "mpg", "mpeg",
            "m2ts", "mts", "ts", "3gp", "3g2", "ogv", "vob", "divx", "rm", "rmvb", "asf", "f4v");

        rules.SeedExtensions(
            FileCategory.Audio,
            "mp3", "m4a", "aac", "flac", "wav", "ogg", "oga", "opus", "wma", "aiff", "aif",
            "alac", "ape", "mka", "mid", "midi", "amr", "ac3", "dts", "weba");

        rules.SeedExtensions(
            FileCategory.Image,
            "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg", "tiff", "tif", "ico", "heic",
            "heif", "avif", "raw", "cr2", "nef", "arw", "dng", "psd", "ai", "eps");

        rules.SeedExtensions(
            FileCategory.Document,
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods",
            "odp", "csv", "tsv", "md", "epub", "mobi", "azw3", "pages", "numbers", "key",
            "tex", "djvu", "xps", "html", "htm", "xml", "json", "log");

        rules.SeedExtensions(
            FileCategory.Compressed,
            "zip", "zipx", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "z",
            "lz", "lzma", "zst", "cab", "arj", "iso", "img", "dmg");

        rules.SeedExtensions(
            FileCategory.Program,
            "exe", "msi", "msix", "appx", "appxbundle", "bat", "cmd", "com", "sh", "run",
            "apk", "aab", "deb", "rpm", "pkg", "bin", "jar", "ps1", "vbs", "scr",
            "application", "snap", "flatpak", "appimage");

        // Exact MIME mappings for application/* (and a few text/* specifics) that the top-level
        // fallback below cannot infer. video/audio/image/text are handled by the prefix rules.
        rules.SeedMimeTypes(
            FileCategory.Document,
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "application/vnd.ms-powerpoint",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            "application/vnd.oasis.opendocument.text",
            "application/vnd.oasis.opendocument.spreadsheet",
            "application/vnd.oasis.opendocument.presentation",
            "application/rtf",
            "application/epub+zip",
            "application/json",
            "application/xml");

        rules.SeedMimeTypes(
            FileCategory.Compressed,
            "application/zip",
            "application/x-zip-compressed",
            "application/x-rar-compressed",
            "application/vnd.rar",
            "application/x-7z-compressed",
            "application/x-tar",
            "application/gzip",
            "application/x-gzip",
            "application/x-bzip2",
            "application/x-xz",
            "application/zstd",
            "application/x-iso9660-image",
            "application/x-apple-diskimage");

        rules.SeedMimeTypes(
            FileCategory.Program,
            "application/x-msdownload",
            "application/x-msdos-program",
            "application/vnd.microsoft.portable-executable",
            "application/x-executable",
            "application/x-elf",
            "application/vnd.android.package-archive",
            "application/x-debian-package",
            "application/x-redhat-package-manager",
            "application/x-rpm",
            "application/java-archive",
            "application/x-sh",
            "application/x-shellscript");

        rules.MapMimeTopLevelType("video", FileCategory.Video);
        rules.MapMimeTopLevelType("audio", FileCategory.Audio);
        rules.MapMimeTopLevelType("image", FileCategory.Image);
        rules.MapMimeTopLevelType("text", FileCategory.Document);

        return rules;
    }

    private void SeedExtensions(FileCategory category, params string[] extensions)
    {
        foreach (string extension in extensions)
        {
            MapExtension(extension, category);
        }
    }

    private void SeedMimeTypes(FileCategory category, params string[] mimeTypes)
    {
        foreach (string mimeType in mimeTypes)
        {
            MapMimeType(mimeType, category);
        }
    }

    private static void ValidateCategory(FileCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(
                nameof(category), category, "Unknown file category.");
        }
    }

    /// <summary>
    /// Reduces an extension input — which may be a leading-dotted token (<c>".mp4"</c>) — to a bare,
    /// lower-invariant key, or <see langword="null"/> when there is nothing usable.
    /// </summary>
    private static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        string value = extension.Trim().TrimStart('.').Trim();
        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    /// <summary>
    /// Strips any <c>; charset=…</c> parameters and lower-cases a MIME type, or returns
    /// <see langword="null"/> when there is nothing usable.
    /// </summary>
    private static string? NormalizeMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return null;
        }

        string value = mimeType.Trim();
        int semicolon = value.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0)
        {
            value = value[..semicolon];
        }

        value = value.Trim();
        return value.Length == 0 ? null : value.ToLowerInvariant();
    }
}
