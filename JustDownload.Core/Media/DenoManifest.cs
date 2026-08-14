namespace JustDownload.Core.Media;

/// <summary>
/// The set of pinned, MIT-licensed Deno builds the engine may download on first use. Deno is the JS-runtime
/// yt-dlp needs to solve YouTube's signature/JS challenges — per yt-dlp's own EJS wiki page, "only deno is
/// enabled by default" among the runtimes it supports, and without one yt-dlp drops the <c>web</c> client
/// entirely, losing signature-protected formats. Provisioning Deno is therefore a dependency of yt-dlp
/// actually working well, not a separate feature — see <see cref="IDenoProvisioner"/> and its wiring into
/// the existing "Download yt-dlp" action.
/// <para>
/// Verified against the GitHub API before pinning: <c>denoland/deno</c>'s <c>license.spdx_id</c> is
/// <c>MIT</c> (checked 2026-08-14). Every entry below is pinned by SHA-256 (CLAUDE.md §4): the hash is the
/// guarantee that the fetched bytes are exactly the reviewed artifact.
/// </para>
/// <para>
/// Unlike ffmpeg's BtbN builds and yt-dlp's own releases, Deno does not publish a single combined checksums
/// file per release — instead each platform asset has its own sibling <c>&lt;asset&gt;.sha256sum</c> file
/// (e.g. <c>deno-x86_64-pc-windows-msvc.zip.sha256sum</c>) alongside it. To refresh this manifest: fetch the
/// latest tag from <c>https://api.github.com/repos/denoland/deno/releases/latest</c>, then for each RID
/// download <c>https://github.com/denoland/deno/releases/download/&lt;tag&gt;/&lt;asset&gt;.zip</c> and its
/// <c>.sha256sum</c> sibling, and independently compute the archive's own SHA-256 to cross-check the
/// published one rather than trusting a single source (done for every RID below on 2026-08-14: the
/// independently-computed hash matched the published <c>.sha256sum</c> file in every case).
/// </para>
/// </summary>
public sealed class DenoManifest
{
    private const string ReleaseTag = "v2.9.5";

    private static Uri ReleaseUrl(string asset) => new(
        $"https://github.com/denoland/deno/releases/download/{ReleaseTag}/{asset}");

    /// <summary>The pinned builds, keyed by runtime identifier.</summary>
    public IReadOnlyList<DenoDownloadSource> Sources { get; }

    public DenoManifest(IReadOnlyList<DenoDownloadSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Sources = sources;
    }

    /// <summary>
    /// The default manifest: Windows, Linux, and macOS x64, plus macOS arm64 (Deno ships separate
    /// per-architecture macOS builds, unlike yt-dlp's universal2 binary). win-arm64 and linux-arm64 are NOT
    /// pinned here — verifying those archives' real hashes was out of scope for this pass; add them
    /// following the refresh process above when needed. Every pinned entry was verified by downloading the
    /// archive and independently computing its SHA-256 against Deno's published <c>.sha256sum</c> (2026-08-14).
    /// </summary>
    public static DenoManifest Default { get; } = new(
    [
        new DenoDownloadSource(
            "win-x64", ReleaseTag, ReleaseUrl("deno-x86_64-pc-windows-msvc.zip"),
            "171efab55ac6b9881fd53ee4c20f8bf3bb1340ffc618483746909014db12216a"),
        new DenoDownloadSource(
            "linux-x64", ReleaseTag, ReleaseUrl("deno-x86_64-unknown-linux-gnu.zip"),
            "8b010a3b1a4a0188a67cdb8a7a27348b2a501af78aec7fc74f2ace167368d530"),
        new DenoDownloadSource(
            "osx-x64", ReleaseTag, ReleaseUrl("deno-x86_64-apple-darwin.zip"),
            "c1b8b89a81e91b2a8b3f96def3195d08cfe3a105651da7908d53061f7140510d"),
        new DenoDownloadSource(
            "osx-arm64", ReleaseTag, ReleaseUrl("deno-aarch64-apple-darwin.zip"),
            "b796aadd131f6930560c1ee040cf0d6f53933fbb987464e9ff46bd7ea4830615"),
    ]);

    /// <summary>Finds the download source for the current platform, if one is pinned.</summary>
    public bool TryGetForCurrentPlatform(out DenoDownloadSource source) =>
        TryGet(FfmpegManifest.CurrentRuntimeIdentifier, out source);

    /// <summary>Finds the download source for <paramref name="runtimeIdentifier"/>, if one is pinned.</summary>
    public bool TryGet(string runtimeIdentifier, out DenoDownloadSource source)
    {
        foreach (DenoDownloadSource candidate in Sources)
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
