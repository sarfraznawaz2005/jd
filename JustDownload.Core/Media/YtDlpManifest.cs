namespace JustDownload.Core.Media;

/// <summary>
/// The set of pinned yt-dlp release binaries the engine may download on first use — only once the user has
/// explicitly enabled video capture/detection in Settings (locked decision D3, TASK-162). yt-dlp is
/// public-domain (Unlicense) and ships one standalone executable per platform, so — unlike
/// <see cref="FfmpegManifest"/> — there is no LGPL/GPL license split to police, only integrity pinning.
/// <para>
/// Pinned to the <c>yt-dlp/yt-dlp-nightly-builds</c> channel, not <c>yt-dlp/yt-dlp</c>'s stable releases
/// (switched 2026-08-14): a real fix Sarfraz needed — Instagram's logged-in-extraction fix, commit
/// <c>1f1101d</c>, merged 2026-07-21 — was not in ANY stable release yet (latest stable at the time was
/// <c>2026.07.04</c>), only in nightly/master. The nightly repo publishes the same asset names and a
/// <c>SHA2-256SUMS</c> file per release as stable does, so this stays a plain static-pinned-hash manifest —
/// no dynamic "always fetch latest" resolver, which would trade away the integrity-pinning guarantee
/// (CLAUDE.md §4) for a bigger architecture change that hasn't been signed off.
/// </para>
/// <para>
/// Nightly builds land far more often than stable (roughly daily vs. roughly weekly), so this manifest goes
/// stale faster than before — an accepted tradeoff for YouTube/Instagram reliability, not a bug. Refresh the
/// tag, URLs, and hashes from a new release's <c>SHA2-256SUMS</c> asset when the pinned one is superseded —
/// see <c>docs/ytdlp.md</c>.
/// </para>
/// </summary>
public sealed class YtDlpManifest
{
    // yt-dlp nightly builds: one standalone executable per platform, pinned to an immutable release tag.
    // Pinned 2026-08-14 to the latest nightly at the time (fetched from
    // https://api.github.com/repos/yt-dlp/yt-dlp-nightly-builds/releases/latest).
    private const string ReleaseTag = "2026.08.04.234419";

    private static Uri ReleaseUrl(string asset) => new(
        $"https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/download/{ReleaseTag}/{asset}");

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
            "e78500d301b5de3a9280a418f6dd45604c4d85b718b0a2447c1b0aa9699e2689"),
        new YtDlpDownloadSource(
            "win-arm64", ReleaseTag, ReleaseUrl("yt-dlp_arm64.exe"),
            "d20b12c8230268dd628620f7a4ba729183c8619e6482dac4460cac5e21b34746"),
        new YtDlpDownloadSource(
            "linux-x64", ReleaseTag, ReleaseUrl("yt-dlp_linux"),
            "5b24dbf54f83a987faaa190c3feb46434fb7f9ed439b2380e070726269f5d026"),
        new YtDlpDownloadSource(
            "linux-arm64", ReleaseTag, ReleaseUrl("yt-dlp_linux_aarch64"),
            "8acff25f8ef5eacfb427b7d6319f06d264a7208dda94ba295deea850a132c0ca"),
        new YtDlpDownloadSource(
            "osx-x64", ReleaseTag, ReleaseUrl("yt-dlp_macos"),
            "a41cb8f4f6362bd5738498ee5e338fe7bd34cf851567afb052a9db2cacf1da07"),
        new YtDlpDownloadSource(
            "osx-arm64", ReleaseTag, ReleaseUrl("yt-dlp_macos"),
            "a41cb8f4f6362bd5738498ee5e338fe7bd34cf851567afb052a9db2cacf1da07"),
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
