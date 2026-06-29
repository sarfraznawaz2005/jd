namespace JustDownload.Core.Media;

/// <summary>The outcome of an ffmpeg run (TASK-040): the exit code and the captured error output.</summary>
/// <param name="ExitCode">ffmpeg's process exit code (<c>0</c> on success).</param>
/// <param name="StandardError">The captured (possibly truncated) stderr, for diagnostics on failure.</param>
public sealed record FfmpegRunResult(int ExitCode, string StandardError)
{
    /// <summary>Whether ffmpeg exited successfully.</summary>
    public bool Succeeded => ExitCode == 0;
}
