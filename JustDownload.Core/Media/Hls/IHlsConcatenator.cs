namespace JustDownload.Core.Media.Hls;

/// <summary>
/// Joins downloaded HLS segments into one <c>.ts</c> file (TASK-038, US-9 AC3). MPEG-TS is designed to be
/// concatenated, so this is an exact byte-for-byte append of each segment in playlist order — no ffmpeg, no
/// re-processing — which makes the output deterministic and byte-identical to a reference concatenation
/// (AC2). Reordering or gaps would corrupt playback, so order is preserved exactly as given.
/// </summary>
public interface IHlsConcatenator
{
    /// <summary>
    /// Concatenates <paramref name="segmentFiles"/>, in the exact order given, into <paramref name="outputPath"/>
    /// and returns it. Each segment is appended whole, with no bytes added or dropped between them.
    /// </summary>
    /// <param name="segmentFiles">The decrypted segment files, in playlist order.</param>
    /// <param name="outputPath">The single <c>.ts</c> file to write.</param>
    /// <param name="progress">Optional sink for cumulative bytes written.</param>
    /// <param name="cancellationToken">Cancels the concatenation (removing the partial output).</param>
    /// <exception cref="ArgumentException"><paramref name="segmentFiles"/> is empty.</exception>
    /// <exception cref="FileNotFoundException">A listed segment file does not exist.</exception>
    Task<string> ConcatenateAsync(
        IReadOnlyList<string> segmentFiles,
        string outputPath,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
}
