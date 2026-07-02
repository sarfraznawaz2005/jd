namespace JustDownload.Core.Media;

/// <summary>
/// The set of pinned yt-dlp release binaries the engine may download on first use — only once the user has
/// explicitly enabled video capture/detection in Settings (locked decision D3, TASK-162). yt-dlp is
/// public-domain (Unlicense) and ships one standalone executable per platform, so — unlike
/// <see cref="FfmpegManifest"/> — there is no LGPL/GPL license split to police, only integrity pinning.
/// <para>
/// yt-dlp cuts releases roughly weekly, so this manifest goes stale faster than ffmpeg's. Refresh the tag,
/// URLs, and hashes from a new release's <c>SHA2-256SUMS</c> asset when the pinned one is superseded — see
/// <c>docs/ytdlp.md</c>.
/// </para>
/// </summary>
public sealed class YtDlpManifest
{
    // yt-dlp GitHub releases: one standalone executable per platform, pinned to an immutable release tag.
    private const string ReleaseTag = "2026.06.09";

    private static Uri ReleaseUrl(string asset) => new(
        $"https://github.com/yt-dlp/yt-dlp/releases/download/{ReleaseTag}/{asset}");

    /// <summary>The pinned standalone builds, keyed by runtime identifier.</summary>
    public IReadOnlyList<YtDlpDownloadSource> Sources { get; }

    public YtDlpManifest(IReadOnlyList<YtDlpDownloadSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Sources = sources;
    }

    /// <summary>
    /// The default manifest: yt-dlp's standalone executables for Windows and Linux (x64 + arm64) and macOS
    /// (one universal2 binary covers both x64 and arm64). Every entry is pinned by the SHA-256 published in
    /// yt-dlp's own <c>SHA2-256SUMS</c> release asset.
    /// </summary>
    public static YtDlpManifest Default { get; } = new(
    [
        new YtDlpDownloadSource(
            "win-x64", ReleaseTag, ReleaseUrl("yt-dlp.exe"),
            "3a48cb955d55c8821b60ccbdbbc6f61bc958f2f3d3b7ad5eaf3d83a543293a27"),
        new YtDlpDownloadSource(
            "win-arm64", ReleaseTag, ReleaseUrl("yt-dlp_arm64.exe"),
            "847583f91bb6d26479c1dc9643c2f4b8857a90b40d619da97b0cfabccb9138d0"),
        new YtDlpDownloadSource(
            "linux-x64", ReleaseTag, ReleaseUrl("yt-dlp_linux"),
            "bf8aac79b72287a6d2043074415132558b43743a8f9461a22b0141e90f16ce66"),
        new YtDlpDownloadSource(
            "linux-arm64", ReleaseTag, ReleaseUrl("yt-dlp_linux_aarch64"),
            "cabd246445bdfde0eda0dfe68bbe90354be83f3fdbbf077df11a2ea55f41cdbd"),
        new YtDlpDownloadSource(
            "osx-x64", ReleaseTag, ReleaseUrl("yt-dlp_macos"),
            "b82c3626952e6c14eaf654cc565866775ffd0b9ffb7021628ac59b42c2f4f244"),
        new YtDlpDownloadSource(
            "osx-arm64", ReleaseTag, ReleaseUrl("yt-dlp_macos"),
            "b82c3626952e6c14eaf654cc565866775ffd0b9ffb7021628ac59b42c2f4f244"),
    ]);

    /// <summary>Finds the download source for the current platform, if one is pinned.</summary>
    public bool TryGetForCurrentPlatform(out YtDlpDownloadSource source) =>
        TryGet(FfmpegManifest.CurrentRuntimeIdentifier, out source);

    /// <summary>Finds the download source for <paramref name="runtimeIdentifier"/>, if one is pinned.</summary>
    public bool TryGet(string runtimeIdentifier, out YtDlpDownloadSource source)
    {
        foreach (YtDlpDownloadSource candidate in Sources)
        {
            if (string.Equals(candidate.RuntimeIdentifier, runtimeIdentifier, StringComparison.OrdinalIgnoreCase))
            {
                source = candidate;
                return true;
            }
        }

        source = null!;
        return false;
    }
}
