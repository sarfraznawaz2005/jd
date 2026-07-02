namespace JustDownload.Core.Media;

/// <summary>
/// A pinned, integrity-checked yt-dlp binary for one runtime identifier (TASK-162, D3). yt-dlp publishes a
/// standalone single-file executable per platform, so download-on-first-use is a plain fetch + SHA-256
/// verify + move into the vendor directory — unlike ffmpeg's zip builds (D7), no archive extraction step is
/// needed.
/// </summary>
/// <param name="RuntimeIdentifier">The .NET RID this build targets, e.g. <c>win-x64</c>.</param>
/// <param name="Version">The yt-dlp release tag, e.g. <c>2026.06.09</c>.</param>
/// <param name="Url">The HTTPS download URL of the standalone executable.</param>
/// <param name="Sha256">The expected lower-case SHA-256 hex digest of the executable.</param>
public sealed record YtDlpDownloadSource(string RuntimeIdentifier, string Version, Uri Url, string Sha256);
