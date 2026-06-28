using System.Security.Cryptography;
using JustDownload.Core.Downloading;
using JustDownload.Tests.Transport;

namespace JustDownload.Tests.Fixtures;

/// <summary>
/// A crash-resume fuzz harness (TASK-083, PRD §5.3): it downloads a resource while killing the transfer at
/// randomized byte offsets, then resumes from the persisted <see cref="ReceivedRanges"/> checkpoint until the
/// file completes, and reports the result so a test can assert the final file is SHA-256-identical to the
/// reference. This exercises the engine's core promise — a crash at any offset never corrupts or re-fetches
/// data (CLAUDE.md §5). All in-process: the loopback server and downloader are torn down by the caller.
/// </summary>
internal static class CrashResumeFuzz
{
    /// <summary>The outcome of a fuzz run.</summary>
    /// <param name="FinalBytes">The bytes of the completed output file.</param>
    /// <param name="Kills">How many times the transfer was killed before it completed.</param>
    /// <param name="Attempts">How many download attempts it took (kills + 1).</param>
    public sealed record Result(byte[] FinalBytes, int Kills, int Attempts);

    /// <summary>
    /// Runs the kill/resume cycle for one download. Each attempt cancels once the cumulative bytes cross a
    /// randomly chosen offset; the next attempt resumes from the checkpoint. Stops when an attempt completes.
    /// </summary>
    public static async Task<Result> RunAsync(
        ISegmentedDownloader downloader,
        LoopbackHttpServer server,
        string destinationPath,
        long totalLength,
        Random random,
        int connections = 4,
        long? speedLimit = null,
        int maxAttempts = 64)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(random);

        var received = new ReceivedRanges();
        int kills = 0;
        int attempts = 0;

        while (attempts < maxAttempts)
        {
            attempts++;
            long alreadyHave = received.TotalReceived;
            long remaining = totalLength - alreadyHave;
            if (remaining <= 0)
            {
                break;
            }

            // Kill somewhere within the still-missing bytes (never at the very end, so a kill really happens).
            long killAt = alreadyHave + 1 + (long)(random.NextDouble() * Math.Max(1, remaining - 1));

            using var cts = new CancellationTokenSource();
            var progress = new CallbackProgress(written =>
            {
                if (written >= killAt)
                {
                    cts.Cancel();
                }
            });

            try
            {
                await downloader.DownloadAsync(
                    new DownloadRequest
                    {
                        Url = server.Url("file.bin"),
                        DestinationPath = destinationPath,
                        Connections = connections,
                        SpeedLimit = speedLimit,
                    },
                    progress,
                    received,
                    cancellationToken: cts.Token).ConfigureAwait(false);

                // Completed without being killed.
                break;
            }
            catch (OperationCanceledException)
            {
                kills++;
                // Loop: resume from the checkpoint recorded in `received`.
            }
        }

        byte[] finalBytes = await File.ReadAllBytesAsync(destinationPath).ConfigureAwait(false);
        return new Result(finalBytes, kills, attempts);
    }

    /// <summary>The lowercase hex SHA-256 of a buffer, for integrity comparison.</summary>
    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>An <see cref="IProgress{T}"/> that invokes its callback synchronously (deterministic kills).</summary>
    private sealed class CallbackProgress : IProgress<long>
    {
        private readonly Action<long> _onReport;

        public CallbackProgress(Action<long> onReport) => _onReport = onReport;

        public void Report(long value) => _onReport(value);
    }
}
