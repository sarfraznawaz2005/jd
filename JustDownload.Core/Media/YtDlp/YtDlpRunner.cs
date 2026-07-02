using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.YtDlp;

/// <summary>
/// Default <see cref="IYtDlpRunner"/> (TASK-163). Spawns yt-dlp with redirected stdout/stderr and
/// guarantees the process (and any children) is killed on cancellation or any failure to start/exit — no
/// orphaned yt-dlp process is ever left running (CLAUDE.md §2.5). Unlike <c>FfmpegRunner</c>, stdout here
/// is a single JSON document (<c>--dump-json</c>), not machine-readable progress lines, so it is read to
/// completion rather than pumped line-by-line; stderr is still captured with a bounded size for
/// diagnostics.
/// </summary>
internal sealed partial class YtDlpRunner : IYtDlpRunner
{
    private const int MaxCapturedErrorChars = 8 * 1024;

    private readonly ILogger<YtDlpRunner> _logger;

    public YtDlpRunner(ILogger<YtDlpRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<YtDlpRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new YtDlpException($"Failed to start yt-dlp at '{executablePath}'.");
        }

        var errorBuffer = new StringBuilder();
        Task<string> outputTask = ReadStandardOutputAsync(process.StandardOutput, cancellationToken);
        Task errorTask = PumpErrorAsync(process.StandardError, errorBuffer, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            if (!SafeHasExited(process))
            {
                TryKill(process);
            }
        }

        string standardOutput = await outputTask.ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);

        LogCompleted(_logger, process.ExitCode);
        return new YtDlpRunResult(process.ExitCode, standardOutput, errorBuffer.ToString());
    }

    private static async Task<string> ReadStandardOutputAsync(
        StreamReader stdout, CancellationToken cancellationToken)
    {
        try
        {
            return await stdout.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static async Task PumpErrorAsync(
        StreamReader stderr, StringBuilder buffer, CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while ((line = await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (buffer.Length < MaxCapturedErrorChars)
                {
                    buffer.AppendLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // The process may have exited between the check and the kill; nothing more to do.
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "yt-dlp exited with code {ExitCode}.")]
    private static partial void LogCompleted(ILogger logger, int exitCode);
}
