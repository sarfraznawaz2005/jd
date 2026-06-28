namespace JustDownload.App.Services;

/// <summary>
/// Extracts an enqueueable URL from data dropped onto the app (TASK-062, US-14). A drop can carry a
/// <c>text/uri-list</c>, plain text, or a single URL; this picks the first absolute http(s)/ftp(s) URL,
/// ignoring comments and non-URL lines. Pure and deterministic so the drop handling is unit-testable
/// independent of the platform drag-and-drop plumbing.
/// </summary>
public static class DroppedLinkParser
{
    private static readonly HashSet<string> SupportedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ftp", "ftps" };

    /// <summary>Returns the first usable URL in <paramref name="text"/>, or <see langword="null"/> if none.</summary>
    public static string? TryExtractUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (string line in text.Split('\n', '\r'))
        {
            string candidate = line.Trim();
            if (candidate.Length == 0 || candidate.StartsWith('#'))
            {
                continue; // uri-list comments start with '#'
            }

            if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) && SupportedSchemes.Contains(uri.Scheme))
            {
                return candidate;
            }
        }

        return null;
    }
}
