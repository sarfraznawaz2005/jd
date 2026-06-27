namespace JustDownload.Core.Categorization;

/// <summary>
/// Default <see cref="IFileCategorizer"/>: a pure resolver over a <see cref="CategorizationRules"/>
/// rule set (TASK-044 / PRD US-8). It holds no mutable state of its own — categorisation is a
/// deterministic function of the inputs and the injected rules — so it is registered as a singleton and
/// is safe to call concurrently.
/// </summary>
internal sealed class FileCategorizer : IFileCategorizer
{
    private static readonly char[] PathSeparators = { '/', '\\' };

    private readonly CategorizationRules _rules;

    public FileCategorizer(CategorizationRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <inheritdoc />
    public FileCategory Categorize(string? fileNameOrExtension, string? contentType = null)
    {
        // Precedence (documented on IFileCategorizer): a recognised extension wins, else the MIME type,
        // else Other. The extension is the file's saved identity and the strongest user-visible signal;
        // the content type is the fallback when the name carries no usable extension.
        string? extensionToken = ExtractExtension(fileNameOrExtension);
        if (_rules.TryGetByExtension(extensionToken, out FileCategory byExtension))
        {
            return byExtension;
        }

        if (_rules.TryGetByMimeType(contentType, out FileCategory byMime))
        {
            return byMime;
        }

        return FileCategory.Other;
    }

    /// <summary>
    /// Pulls the extension token out of a file name, path, or bare extension. Returns the dotted
    /// extension (e.g. <c>".mp4"</c>) when one is present, the input itself when it is a clean bare
    /// token (<c>"mp4"</c>), or <see langword="null"/> when there is nothing usable. The rule set
    /// normalises the leading dot and case, so either form resolves identically.
    /// </summary>
    private static string? ExtractExtension(string? fileNameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension))
        {
            return null;
        }

        string value = fileNameOrExtension.Trim();
        string extension = Path.GetExtension(value);
        if (extension.Length > 0)
        {
            return extension;
        }

        // No dot: accept a clean single token (no path separators) as the extension itself.
        if (value.IndexOf('.', StringComparison.Ordinal) < 0
            && value.IndexOfAny(PathSeparators) < 0)
        {
            return value;
        }

        return null;
    }
}
