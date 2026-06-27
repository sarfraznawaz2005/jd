using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace JustDownload.Core.Storage;

/// <summary>
/// The download's output file (TASK-025): pre-sized to the total length (sparse where the OS supports
/// it) and written at explicit byte offsets so a segmented download's many connections can write their
/// own ranges concurrently. Writes go through <see cref="RandomAccess"/> on a shared
/// <see cref="SafeFileHandle"/>, which is safe for concurrent non-overlapping positioned writes and
/// never touches a shared stream position. The streaming copy path rents from
/// <see cref="ArrayPool{T}"/> with a sub-LOH buffer, so steady-state downloading does no large-object
/// allocations.
/// </summary>
public sealed class PreallocatedFile : IAsyncDisposable, IDisposable
{
    /// <summary>The pooled copy buffer size (64 KiB) — comfortably below the 85 000-byte LOH threshold.</summary>
    public const int CopyBufferSize = 64 * 1024;

    private readonly SafeFileHandle _handle;
    private int _disposed;

    private PreallocatedFile(SafeFileHandle handle, string path, long length)
    {
        _handle = handle;
        Path = path;
        Length = length;
    }

    /// <summary>The absolute path of the output file.</summary>
    public string Path { get; }

    /// <summary>The total length the file was pre-allocated to.</summary>
    public long Length { get; }

    /// <summary>
    /// Creates (or opens) the file at <paramref name="path"/>, marks it sparse where supported, and
    /// pre-allocates it to <paramref name="totalLength"/> bytes. The parent directory is created if
    /// missing. Re-opening an existing file of the right size (a resume) is a no-op resize.
    /// </summary>
    public static PreallocatedFile Create(string path, long totalLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(totalLength);

        string fullPath = System.IO.Path.GetFullPath(path);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            FileOptions.Asynchronous);
        try
        {
            NativeFile.TryMarkSparse(handle);
            RandomAccess.SetLength(handle, totalLength);
            return new PreallocatedFile(handle, fullPath, totalLength);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Writes <paramref name="buffer"/> at absolute <paramref name="offset"/>.</summary>
    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken);
    }

    /// <summary>
    /// Streams <paramref name="source"/> into the file starting at <paramref name="offset"/>, using a
    /// pooled buffer, and returns the number of bytes written. <paramref name="progress"/> receives the
    /// running total. This is the hot download-copy path for one segment's connection.
    /// </summary>
    public async Task<long> CopyFromAsync(
        Stream source,
        long offset,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        byte[] rented = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long position = offset;
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(rented.AsMemory(0, CopyBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(_handle, rented.AsMemory(0, read), position, cancellationToken)
                    .ConfigureAwait(false);
                position += read;
                total += read;
                progress?.Report(total);
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Flushes written bytes to physical storage (used before a durable checkpoint).</summary>
    public Task FlushToDiskAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeFile.FlushToDisk(_handle);
            },
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
