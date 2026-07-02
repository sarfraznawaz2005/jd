using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IYtDlpLocator"/> (TASK-162, D3). Tries the configured path, then the vendor directory,
/// then the system <c>PATH</c>, running <c>yt-dlp --version</c> on each candidate. Running that command
/// successfully <em>is</em> the self-validation — a candidate that starts but exits non-zero, or isn't
/// executable at all, simply isn't a working yt-dlp. The first candidate that runs is cached.
/// </summary>
internal sealed partial class YtDlpLocator : IYtDlpLocator, IDisposable
{
    private readonly YtDlpOptions _options;
    private readonly ILogger<YtDlpLocator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private YtDlpInfo? _cached;

    public YtDlpLocator(YtDlpOptions options, ILogger<YtDlpLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    public void Dispose() => _gate.Dispose();

    public async Task<YtDlpInfo?> LocateAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            foreach (string candidate in Candidates())
            {
                string? version = await TryReadVersionAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (version is not null)
                {
                    _cached = new YtDlpInfo(candidate, version);
                    LogLocated(_logger, candidate, version);
                    return _cached;
                }
            }

            LogNotFound(_logger);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(_options.YtDlpPath))
        {
            yield return _options.YtDlpPath;
        }

        if (!string.IsNullOrWhiteSpace(_options.VendorDirectory))
        {
            yield return Path.Combine(_options.VendorDirectory, ExecutableName);
        }

        yield return ExecutableName; // resolved via PATH
    }

    private static async Task<string?> TryReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            string firstLine = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? string.Empty;
            string remainder = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _ = remainder; // drain so the process can exit
            _ = stderr;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            string version = firstLine.Trim();
            return process.ExitCode == 0 && version.Length > 0 ? version : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A candidate that doesn't exist / isn't executable simply isn't yt-dlp; try the next one.
            return null;
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Located yt-dlp {Path} (version {Version}).")]
    private static partial void LogLocated(ILogger logger, string path, string version);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "yt-dlp was not found on this system.")]
    private static partial void LogNotFound(ILogger logger);
}
