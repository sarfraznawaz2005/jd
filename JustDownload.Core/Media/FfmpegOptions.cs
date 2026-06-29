namespace JustDownload.Core.Media;

/// <summary>
/// Configuration for locating ffmpeg/ffprobe (TASK-040, LOCKED DECISION D7). When the explicit paths are
/// unset the locator falls back to a bundled <c>vendor/</c> directory and then the system <c>PATH</c>.
/// Only an LGPL build of ffmpeg is ever shipped, invoked as a separate process (CLAUDE.md §4).
/// </summary>
public sealed class FfmpegOptions
{
    /// <summary>Explicit path to the ffmpeg executable, or <see langword="null"/> to auto-locate.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Explicit path to the ffprobe executable, or <see langword="null"/> to auto-locate.</summary>
    public string? FfprobePath { get; set; }

    /// <summary>An optional directory (e.g. a bundled <c>vendor/ffmpeg</c>) searched before <c>PATH</c>.</summary>
    public string? VendorDirectory { get; set; }
}
