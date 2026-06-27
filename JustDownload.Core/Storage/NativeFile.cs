using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JustDownload.Core.Storage;

/// <summary>
/// Thin OS interop for the download file writer (TASK-025): marking a file sparse (so a multi-gigabyte
/// download is not physically pre-allocated up front — supporting the light-&amp;-fast promise) and
/// flushing OS buffers to disk for crash-durable checkpoints. All P/Invokes are source-generated
/// (<see cref="LibraryImportAttribute"/>) and guarded by an <see cref="OperatingSystem"/> check.
/// </summary>
internal static partial class NativeFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    /// <summary>
    /// Best-effort: marks the file sparse on Windows/NTFS so <c>SetLength</c> reserves logical size
    /// without allocating clusters. A no-op (and harmless) on other filesystems; on Unix a truncated
    /// file is already sparse, so nothing is needed there.
    /// </summary>
    public static void TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    /// <summary>Flushes the file's OS buffers to physical storage (<c>FlushFileBuffers</c>/<c>fsync</c>).</summary>
    public static void FlushToDisk(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
            {
                throw new IOException(
                    $"FlushFileBuffers failed (error {Marshal.GetLastPInvokeError()}).");
            }

            return;
        }

        bool refAdded = false;
        try
        {
            handle.DangerousAddRef(ref refAdded);
            int fd = (int)handle.DangerousGetHandle();
            if (Fsync(fd) != 0)
            {
                throw new IOException($"fsync failed (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            if (refAdded)
            {
                handle.DangerousRelease();
            }
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle hFile);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);
}
