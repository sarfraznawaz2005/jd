using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IFfmpegLocator"/> (TASK-040). Tries the configured path, then a bundled vendor
/// directory, then the system <c>PATH</c>, running <c>ffmpeg -version</c> on each candidate and parsing
/// the version from the first line. The first candidate that runs is cached.
/// </summary>
internal sealed partial class FfmpegLocator : IFfmpegLocator, IDisposable
{
    private readonly FfmpegOptions _options;
    private readonly ILogger<FfmpegLocator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FfmpegInfo? _cached;

    public FfmpegLocator(FfmpegOptions options, ILogger<FfmpegLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public void Dispose() => _gate.Dispose();

    public async Task<FfmpegInfo?> LocateAsync(CancellationToken cancellationToken = default)
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
                    _cached = new FfmpegInfo(candidate, version);
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
        if (!string.IsNullOrWhiteSpace(_options.FfmpegPath))
        {
            yield return _options.FfmpegPath;
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
            startInfo.ArgumentList.Add("-version");

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            string firstLine = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                ?? string.Empty;
            string remainder = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _ = remainder; // drain so the process can exit
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0 ? ParseVersion(firstLine) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A candidate that doesn't exist / isn't executable simply isn't ffmpeg; try the next one.
            return null;
        }
    }

    /// <summary>Extracts the version token from an <c>ffmpeg version X.Y.Z-…</c> banner line.</summary>
    internal static string? ParseVersion(string versionLine)
    {
        const string prefix = "ffmpeg version ";
        if (string.IsNullOrWhiteSpace(versionLine) ||
            !versionLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = versionLine[prefix.Length..].Trim();
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? rest[..space] : rest;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Located ffmpeg {Path} (version {Version}).")]
    private static partial void LogLocated(ILogger logger, string path, string version);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "ffmpeg was not found on this system.")]
    private static partial void LogNotFound(ILogger logger);
}
