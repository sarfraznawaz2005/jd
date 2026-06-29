using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;

namespace JustDownload.Core.Integrity;

/// <summary>The outcome of comparing a file against an expected hash (TASK-132).</summary>
public enum ChecksumOutcome
{
    /// <summary>The file's hash equals the expected hash.</summary>
    Match,

    /// <summary>The file's hash differs from the expected hash — the download is corrupt or wrong.</summary>
    Mismatch,

    /// <summary>The expected hash was not a recognised MD5 (32) or SHA-256 (64) hex digest.</summary>
    UnrecognizedHashFormat,

    /// <summary>The file to verify does not exist.</summary>
    FileNotFound,
}

/// <summary>The result of a verification: the <see cref="Outcome"/> and, when computed, the file's hash.</summary>
public sealed record ChecksumResult(ChecksumOutcome Outcome, string? ComputedHash)
{
    /// <summary>Whether the file matched the expected hash.</summary>
    public bool IsMatch => Outcome == ChecksumOutcome.Match;
}

/// <summary>
/// Verifies a completed download against a user-supplied or page-parsed MD5/SHA-256 hash (TASK-132). The
/// algorithm is inferred from the hex digest length (32 → MD5, 64 → SHA-256); comparison is
/// case-insensitive and ignores surrounding whitespace. The file is streamed through a pooled buffer so
/// verifying a large file never loads it into memory.
/// </summary>
public interface IChecksumVerifier
{
    Task<ChecksumResult> VerifyAsync(string filePath, string expectedHash, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IChecksumVerifier"/> over <see cref="IncrementalHash"/>.</summary>
public sealed class ChecksumVerifier : IChecksumVerifier
{
    private const int BufferSize = 64 * 1024; // sub-LOH, matches the download copy buffer

    public async Task<ChecksumResult> VerifyAsync(
        string filePath, string expectedHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(expectedHash);

        string normalized = expectedHash.Trim();
        HashAlgorithmName? algorithm = AlgorithmForLength(normalized.Length);
        if (algorithm is null || !IsHex(normalized))
        {
            return new ChecksumResult(ChecksumOutcome.UnrecognizedHashFormat, null);
        }

        if (!File.Exists(filePath))
        {
            return new ChecksumResult(ChecksumOutcome.FileNotFound, null);
        }

        string computed = await ComputeAsync(filePath, algorithm.Value, cancellationToken).ConfigureAwait(false);
        bool match = string.Equals(computed, normalized, StringComparison.OrdinalIgnoreCase);
        return new ChecksumResult(match ? ChecksumOutcome.Match : ChecksumOutcome.Mismatch, computed);
    }

    private static HashAlgorithmName? AlgorithmForLength(int hexLength) => hexLength switch
    {
        32 => HashAlgorithmName.MD5,
        64 => HashAlgorithmName.SHA256,
        _ => null,
    };

    private static async Task<string> ComputeAsync(
        string filePath, HashAlgorithmName algorithm, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(algorithm);
        await using FileStream stream = new(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
