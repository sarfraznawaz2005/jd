namespace JustDownload.Core.Media.YtDlp;

/// <summary>
/// The outcome of one yt-dlp invocation (TASK-163): its exit code, the captured stdout (the JSON document
/// for a <c>--dump-json</c> run), and a bounded capture of stderr for diagnostics.
/// </summary>
/// <param name="ExitCode">The process exit code; <c>0</c> is success for yt-dlp.</param>
/// <param name="StandardOutput">The full captured stdout.</param>
/// <param name="StandardError">Stderr, truncated to a bounded size.</param>
public sealed record YtDlpRunResult(int ExitCode, string StandardOutput, string StandardError);
