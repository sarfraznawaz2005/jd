using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IFfmpegRunner"/> (TASK-040). Spawns ffmpeg with machine-readable progress on
/// stdout (<c>-progress pipe:1</c>) and errors on stderr, pumps both asynchronously, and guarantees the
/// process is killed (with its children) on cancellation or any failure — no orphaned ffmpeg is ever
/// left holding the output file (CLAUDE.md §2.5).
/// </summary>
internal sealed partial class FfmpegRunner : IFfmpegRunner
{
    private const int MaxCapturedErrorChars = 8 * 1024;

    private static readonly string[] GlobalArguments =
        ["-hide_banner", "-loglevel", "error", "-nostats", "-progress", "pipe:1"];

    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegRunner> _logger;

    public FfmpegRunner(IFfmpegLocator locator, ILogger<FfmpegRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(logger);
        _locator = locator;
        _logger = logger;
    }

    public async Task<FfmpegRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        FfmpegInfo info = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new FfmpegException("ffmpeg executable was not found.");

        var startInfo = new ProcessStartInfo
        {
            FileName = info.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string global in GlobalArguments)
        {
            startInfo.ArgumentList.Add(global);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new FfmpegException("Failed to start ffmpeg.");
        }

        var errorBuffer = new StringBuilder();
        Task progressTask = PumpProgressAsync(process.StandardOutput, progress, cancellationToken);
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

        await Task.WhenAll(progressTask, errorTask).ConfigureAwait(false);

        LogCompleted(_logger, process.ExitCode);
        return new FfmpegRunResult(process.ExitCode, errorBuffer.ToString());
    }

    private static async Task PumpProgressAsync(
        StreamReader stdout, IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            // Still drain stdout so the process can exit and the pipe never blocks.
            try
            {
                await stdout.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            return;
        }

        var reader = new FfmpegProgressReader();
        try
        {
            string? line;
            while ((line = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (reader.Push(line, out FfmpegProgress snapshot))
                {
                    progress.Report(snapshot);
                }
            }
        }
        catch (OperationCanceledException)
        {
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "ffmpeg exited with code {ExitCode}.")]
    private static partial void LogCompleted(ILogger logger, int exitCode);
}
