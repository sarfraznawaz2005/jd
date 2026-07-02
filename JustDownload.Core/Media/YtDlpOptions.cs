namespace JustDownload.Core.Media;

/// <summary>
/// Configuration for locating yt-dlp (TASK-162, locked decision D3). When the explicit path is unset the
/// locator falls back to a downloaded <c>vendor/</c> directory and then the system <c>PATH</c>. yt-dlp is
/// invoked as a separate process, same as ffmpeg (D7) — never bundled or statically linked.
/// </summary>
public sealed class YtDlpOptions
{
    /// <summary>Explicit path to the yt-dlp executable, or <see langword="null"/> to auto-locate.</summary>
    public string? YtDlpPath { get; set; }

    /// <summary>An optional directory (e.g. the downloaded <c>vendor/yt-dlp</c>) searched before <c>PATH</c>.</summary>
    public string? VendorDirectory { get; set; }
}
