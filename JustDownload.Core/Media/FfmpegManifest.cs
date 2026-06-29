using System.Runtime.InteropServices;

namespace JustDownload.Core.Media;

/// <summary>
/// The set of pinned, LGPL ffmpeg builds the engine may download on first use (TASK-079). The default
/// manifest covers the platforms where ffmpeg is not normally pre-installed (Windows); on Linux/macOS the
/// system package manager already provides ffmpeg, so the locator's <c>PATH</c> search is preferred and no
/// auto-download source is listed (the provisioner degrades gracefully with a "install ffmpeg" message).
/// <para>
/// Every entry is an LGPL build pinned by SHA-256 (D7 / CLAUDE.md §4): the hash is the guarantee that the
/// fetched bytes are exactly the reviewed LGPL artifact, so a GPL build can never be substituted (AC1).
/// Refresh the URLs/hashes when BtbN prunes an autobuild tag — see <c>docs/ffmpeg.md</c>.
/// </para>
/// </summary>
public sealed class FfmpegManifest
{
    // BtbN FFmpeg-Builds, LGPL "shared" Windows builds (separate ffmpeg.exe + libraries, invoked as a
    // child process). Pinned to an immutable dated autobuild so the SHA-256 stays valid.
    private const string BtbnTag = "autobuild-2026-06-29-14-25";
    private const string BtbnBuild = "ffmpeg-n7.1.5-1-g7d0e842004";

    private static Uri BtbnUrl(string platform) => new(
        $"https://github.com/BtbN/FFmpeg-Builds/releases/download/{BtbnTag}/{BtbnBuild}-{platform}-lgpl-shared-7.1.zip");

    /// <summary>The pinned LGPL builds, keyed by runtime identifier.</summary>
    public IReadOnlyList<FfmpegDownloadSource> Sources { get; }

    public FfmpegManifest(IReadOnlyList<FfmpegDownloadSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Sources = sources;
    }

    /// <summary>
    /// The default manifest: the pinned LGPL Windows x64 build (AC0/AC1). Other RIDs fall back to the
    /// system <c>PATH</c>; add a pinned source per the refresh process in <c>docs/ffmpeg.md</c>.
    /// </summary>
    public static FfmpegManifest Default { get; } = new(
    [
        new FfmpegDownloadSource(
            "win-x64", "n7.1", FfmpegBuildLicense.Lgpl, BtbnUrl("win64"),
            "eb4453bb655d23592beccd6e38401a0c3981fcaf620e8677affc56fdd81e40e6"),
    ]);

    /// <summary>
    /// The runtime identifier of the current process (<c>os-arch</c>, e.g. <c>win-x64</c>), used to select
    /// a download source. Mirrors the RIDs in <see cref="Default"/> so a match is exact.
    /// </summary>
    public static string CurrentRuntimeIdentifier
    {
        get
        {
            string os =
                OperatingSystem.IsWindows() ? "win" :
                OperatingSystem.IsMacOS() ? "osx" :
                OperatingSystem.IsLinux() ? "linux" :
                "unknown";

            string arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };

            return $"{os}-{arch}";
        }
    }

    /// <summary>Finds the download source for the current platform, if one is pinned.</summary>
    public bool TryGetForCurrentPlatform(out FfmpegDownloadSource source) =>
        TryGet(CurrentRuntimeIdentifier, out source);

    /// <summary>Finds the download source for <paramref name="runtimeIdentifier"/>, if one is pinned.</summary>
    public bool TryGet(string runtimeIdentifier, out FfmpegDownloadSource source)
    {
        foreach (FfmpegDownloadSource candidate in Sources)
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
