namespace JustDownload.Core.Media;

/// <summary>A located yt-dlp executable and its reported version (TASK-162, D3).</summary>
/// <param name="ExecutablePath">The path/name used to invoke yt-dlp (may resolve via <c>PATH</c>).</param>
/// <param name="Version">The version string reported by <c>yt-dlp --version</c>, e.g. <c>2026.06.09</c>.</param>
public sealed record YtDlpInfo(string ExecutablePath, string Version);
