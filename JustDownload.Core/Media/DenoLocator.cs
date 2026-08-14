using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media;

/// <summary>
/// Default <see cref="IDenoLocator"/>. Tries the configured path, then the vendor directory, then the
/// system <c>PATH</c>, running <c>deno --version</c> on each candidate and parsing the version from the
/// first line. The first candidate that runs is cached.
/// </summary>
internal sealed partial class DenoLocator : IDenoLocator, IDisposable
{
    private readonly DenoOptions _options;
    private readonly ILogger<DenoLocator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DenoInfo? _cached;

    public DenoLocator(DenoOptions options, ILogger<DenoLocator> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "deno.exe" : "deno";

    public void Dispose() => _gate.Dispose();

    public async Task<DenoInfo?> LocateAsync(CancellationToken cancellationToken = default)
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
                    _cached = new DenoInfo(candidate, version);
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

    public void Invalidate()
    {
        _gate.Wait();
        try
        {
            _cached = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrWhiteSpace(_options.DenoPath))
        {
            yield return _options.DenoPath;
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

            return process.ExitCode == 0 ? ParseVersion(firstLine) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A candidate that doesn't exist / isn't executable simply isn't Deno; try the next one.
            return null;
        }
    }

    /// <summary>Extracts the version token from a <c>deno X.Y.Z (release, ...)</c> banner line.</summary>
    internal static string? ParseVersion(string versionLine)
    {
        const string prefix = "deno ";
        if (string.IsNullOrWhiteSpace(versionLine) ||
            !versionLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string rest = versionLine[prefix.Length..].Trim();
        int space = rest.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? rest[..space] : rest;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Located Deno {Path} (version {Version}).")]
    private static partial void LogLocated(ILogger logger, string path, string version);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Deno was not found on this system.")]
    private static partial void LogNotFound(ILogger logger);
}
