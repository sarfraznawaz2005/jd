namespace JustDownload.Core.Media;

/// <summary>The licensing of an ffmpeg build (TASK-079, D7 / CLAUDE.md §4).</summary>
public enum FfmpegBuildLicense
{
    /// <summary>An LGPL build — the only kind JustDownload may ever obtain or ship.</summary>
    Lgpl,

    /// <summary>
    /// A GPL build. Never downloaded by the app; present only so the provisioner can refuse it
    /// (a GPL build would force the whole MIT app to GPL, D2/§4).
    /// </summary>
    Gpl,
}

/// <summary>
/// A pinned, integrity-checked ffmpeg archive for one runtime identifier (TASK-079). Download-on-first-use
/// fetches the <see cref="Url"/>, verifies it against <see cref="Sha256"/>, and extracts the executables
/// (and any side-by-side libraries) into the vendor directory. Only <see cref="FfmpegBuildLicense.Lgpl"/>
/// sources are ever fetched — the pinned hash guarantees the bytes are exactly the LGPL build that was
/// reviewed, so no GPL build can be substituted (AC1, D7 / CLAUDE.md §4).
/// </summary>
/// <param name="RuntimeIdentifier">The .NET RID this build targets, e.g. <c>win-x64</c>.</param>
/// <param name="Version">The ffmpeg version, e.g. <c>n7.1</c>.</param>
/// <param name="License">The build's license; must be <see cref="FfmpegBuildLicense.Lgpl"/> to be fetched.</param>
/// <param name="Url">The HTTPS download URL of the (zip) archive.</param>
/// <param name="Sha256">The expected lower-case SHA-256 hex digest of the archive.</param>
public sealed record FfmpegDownloadSource(
    string RuntimeIdentifier,
    string Version,
    FfmpegBuildLicense License,
    Uri Url,
    string Sha256);
