using System.Net.Http.Headers;

namespace JustDownload.Core.Transport;

/// <summary>
/// Derives a download's file name from the <c>Content-Disposition</c> header, falling back to the URL
/// (TASK-023 AC1). Pure and deterministic so it is unit-testable in isolation. The result is always a
/// bare, filesystem-safe file name — any directory component is stripped and invalid characters are
/// removed — so a hostile <c>filename</c> (e.g. <c>"../../etc/passwd"</c>) can never escape the target
/// directory.
/// </summary>
public static class HttpFileNameResolver
{
    private const string DefaultFileName = "download";

    /// <summary>
    /// Extracts the file name from a raw <c>Content-Disposition</c> header value, preferring the RFC 5987
    /// <c>filename*</c> form over <c>filename</c>. Returns <see langword="null"/> when the header is
    /// absent, unparseable, or carries no usable name.
    /// </summary>
    /// <param name="headerValue">The raw header value, e.g. <c>attachment; filename="report.pdf"</c>.</param>
    public static string? FromContentDisposition(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        ContentDispositionHeaderValue? parsed;
        try
        {
            parsed = ContentDispositionHeaderValue.Parse(headerValue);
        }
        catch (FormatException)
        {
            return null;
        }

        // filename* (already RFC 5987-decoded by the parser) takes precedence over filename. Note that
        // ContentDispositionHeaderValue.FileName returns the value WITH its surrounding quotes, so strip
        // a single enclosing pair before sanitising.
        string? candidate = parsed.FileNameStar ?? StripEnclosingQuotes(parsed.FileName);
        return Sanitize(candidate);
    }

    /// <summary>
    /// Derives a file name from the last path segment of a URL (URL-decoded), or the host, or the
    /// <see cref="DefaultFileName"/> default. Never returns <see langword="null"/>.
    /// </summary>
    /// <param name="uri">The (final, post-redirect) resource URL.</param>
    public static string FromUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
        int lastSlash = path.LastIndexOf('/');
        string segment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;

        string decoded = Uri.UnescapeDataString(segment);
        string? safe = Sanitize(decoded);
        if (safe is not null)
        {
            return safe;
        }

        // No usable path segment (e.g. "https://host/"): fall back to the host, then a constant.
        string? host = uri.IsAbsoluteUri ? Sanitize(uri.Host) : null;
        return host ?? DefaultFileName;
    }

    /// <summary>
    /// Resolves the file name to use: the <c>Content-Disposition</c> name when present, otherwise the
    /// URL-derived name (TASK-023 AC1). Always returns a non-empty, filesystem-safe name.
    /// </summary>
    /// <param name="contentDisposition">The raw <c>Content-Disposition</c> header value, if any.</param>
    /// <param name="finalUri">The resource URL after redirects.</param>
    public static string Resolve(string? contentDisposition, Uri finalUri)
    {
        ArgumentNullException.ThrowIfNull(finalUri);
        return FromContentDisposition(contentDisposition) ?? FromUri(finalUri);
    }

    /// <summary>Removes a single pair of enclosing double quotes, if present.</summary>
    private static string? StripEnclosingQuotes(string? value)
    {
        if (value is { Length: >= 2 } && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
    }

    /// <summary>
    /// Reduces a candidate to a bare, filesystem-safe file name: strips any path, removes characters the
    /// OS forbids, trims trailing dots/spaces, and rejects "." / "..". Returns <see langword="null"/>
    /// when nothing usable remains.
    /// </summary>
    private static string? Sanitize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        // Strip any directory component using both separators (Path.GetFileName is platform-specific).
        string name = candidate.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char ch in name)
        {
            builder.Append(Array.IndexOf(InvalidChars, ch) >= 0 ? '_' : ch);
        }

        // Trailing dots and spaces are invalid on Windows; "." and ".." are not real names.
        string cleaned = builder.ToString().Trim().TrimEnd('.', ' ').Trim();
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..")
        {
            return null;
        }

        return cleaned;
    }

    private static readonly char[] InvalidChars = BuildInvalidChars();

    private static char[] BuildInvalidChars()
    {
        // Union of the OS-invalid set with characters that are invalid on other platforms (so a name is
        // safe cross-platform regardless of where it was derived) and control characters.
        var chars = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (char ch in "<>:\"/\\|?*")
        {
            chars.Add(ch);
        }

        for (char ch = '\0'; ch < ' '; ch++)
        {
            chars.Add(ch);
        }

        return [.. chars];
    }
}
