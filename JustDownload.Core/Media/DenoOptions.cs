namespace JustDownload.Core.Media;

/// <summary>
/// Configuration for locating Deno — the JS-runtime yt-dlp uses to solve YouTube's signature/JS challenges
/// (only <c>deno</c> is enabled by default among yt-dlp's supported runtimes). When the explicit path is
/// unset the locator falls back to a downloaded <c>vendor/</c> directory and then the system <c>PATH</c>.
/// Deno is invoked as a separate process, same as ffmpeg (D7) and yt-dlp — never bundled or statically linked.
/// </summary>
public sealed class DenoOptions
{
    /// <summary>Explicit path to the Deno executable, or <see langword="null"/> to auto-locate.</summary>
    public string? DenoPath { get; set; }

    /// <summary>An optional directory (e.g. the downloaded <c>vendor/deno</c>) searched before <c>PATH</c>.</summary>
    public string? VendorDirectory { get; set; }
}
