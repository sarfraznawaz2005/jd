namespace JustDownload.Core.Media;

/// <summary>A located ffmpeg executable and its reported version (TASK-040).</summary>
/// <param name="ExecutablePath">The path/name used to invoke ffmpeg (may resolve via <c>PATH</c>).</param>
/// <param name="Version">The version string parsed from <c>ffmpeg -version</c>, e.g. <c>7.1.1</c>.</param>
public sealed record FfmpegInfo(string ExecutablePath, string Version);
