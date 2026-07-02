namespace JustDownload.Core.Media.YtDlp;

/// <summary>
/// Spawns the yt-dlp executable as a separate process and captures its output (TASK-163, D3). Mirrors
/// <c>FfmpegRunner</c>'s external-process pattern (TASK-040): redirected stdout/stderr, kill-tree on
/// cancellation or any non-normal exit, no orphaned process left behind (CLAUDE.md §2.5). The seam is kept
/// separate from <c>IYtDlpLocator</c>/<c>IYtDlpProvisioner</c> (which only resolve/acquire the binary) so
/// callers can mock process invocation in tests without touching a real yt-dlp binary.
/// </summary>
public interface IYtDlpRunner
{
    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/> to completion and returns
    /// its captured output. Never throws for a non-zero exit — that is reported via
    /// <see cref="YtDlpRunResult.ExitCode"/> so the caller decides how to react.
    /// </summary>
    /// <param name="executablePath">The yt-dlp executable to run (from <c>IYtDlpLocator</c>).</param>
    /// <param name="arguments">The command-line arguments, in order.</param>
    /// <param name="cancellationToken">Cancelling kills the process (and its children) immediately.</param>
    Task<YtDlpRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}
