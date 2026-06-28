using System.Buffers;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Media.Hls;

/// <summary>
/// Default <see cref="IHlsConcatenator"/> (TASK-038). Streams each segment into the output in order using a
/// pooled buffer (<see cref="ArrayPool{T}"/>, CLAUDE.md §5 "light by default") so memory stays flat for an
/// arbitrarily long stream. Pure append — the concatenated bytes equal a manual <c>cat seg0 seg1 …</c>, so
/// the result is byte-identical to the reference (AC2). On cancellation or failure the partial output is
/// removed so a half-written file is never left behind.
/// </summary>
internal sealed partial class HlsConcatenator : IHlsConcatenator
{
    private const int BufferSize = 81920;

    private readonly ILogger<HlsConcatenator> _logger;

    public HlsConcatenator(ILogger<HlsConcatenator> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<string> ConcatenateAsync(
        IReadOnlyList<string> segmentFiles,
        string outputPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segmentFiles);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (segmentFiles.Count == 0)
        {
            throw new ArgumentException("At least one segment is required to concatenate.", nameof(segmentFiles));
        }

        foreach (string segment in segmentFiles)
        {
            if (!File.Exists(segment))
            {
                throw new FileNotFoundException("HLS segment to concatenate was not found.", segment);
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long written = 0;
        try
        {
            await using (var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                foreach (string segment in segmentFiles)
                {
                    await using var input = new FileStream(
                        segment, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);

                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
                        .ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        progress?.Report(written);
                    }
                }
            }
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        LogConcatenated(_logger, segmentFiles.Count, written, outputPath);
        return outputPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Concatenated {Count} segments ({Bytes} bytes) → {Output}.")]
    private static partial void LogConcatenated(ILogger logger, int count, long bytes, string output);
}
