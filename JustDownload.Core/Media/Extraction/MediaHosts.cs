namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// The "should we even try extraction" gate for page URLs (TASK-265). Covers the four families the
/// in-house extractors already accept — YouTube (<c>youtube.com</c>/<c>*.youtube.com</c>/<c>youtu.be</c>),
/// X (<c>x.com</c>/<c>twitter.com</c> and their subdomains), Facebook
/// (<c>facebook.com</c>/<c>*.facebook.com</c>/<c>fb.watch</c>), and Instagram
/// (<c>instagram.com</c>/<c>*.instagram.com</c>) — mirroring the browser extension's
/// <c>EXTRACTABLE_HOSTS</c>. This is a coarse host allowlist only: the per-extractor <c>LooksLikeX</c>
/// methods remain the precise recognizers.
/// <para>
/// The gate is mandatory, not a nicety: <see cref="YtDlpMediaExtractor"/> is a catch-all with
/// <c>Priority = int.MaxValue</c> and no host check, so it would spawn <c>yt-dlp</c> for *any* URL handed
/// to the registry. Routing an arbitrary pasted file URL through the registry would therefore launch
/// yt-dlp on every plain-file paste (breaching AC2), which is exactly what this allowlist prevents —
/// only URLs on a known video host ever reach the registry.
/// </para>
/// </summary>
internal static class MediaHosts
{
    // "host" matches the host exactly (e.g. "x.com"); ".host" matches any subdomain of it. The list is the
    // union of what the in-house extractors recognise plus the extension's EXTRACTABLE_HOSTS.
    private static readonly string[] KnownVideoHosts =
    [
        "youtube.com", "youtu.be",
        "x.com", "twitter.com",
        "facebook.com", "fb.watch",
        "instagram.com",
    ];

    public static bool IsKnownVideoHost(Uri uri)
    {
        if (uri is not { IsAbsoluteUri: true })
        {
            return false;
        }

        string host = uri.Host;
        foreach (string known in KnownVideoHosts)
        {
            if (host.Equals(known, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
