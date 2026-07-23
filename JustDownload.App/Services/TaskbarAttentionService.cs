using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace JustDownload.App.Services;

/// <summary>
/// Default <see cref="ITaskbarAttention"/> (TASK-226). On Windows it calls <c>FlashWindowEx</c>, which flashes
/// the taskbar button and title bar until the window is brought forward — attention without focus theft.
/// <para>
/// macOS and Linux have no equivalent Avalonia 11 exposes: macOS's <c>NSApp.requestUserAttention</c> and the
/// freedesktop <c>_NET_WM_STATE_DEMANDS_ATTENTION</c> hint are both out of reach without extra native interop,
/// so <see cref="IsSupported"/> is <see langword="false"/> there and the completion toast (TASK-123) carries
/// the notification on its own. Stated plainly rather than failing quietly (§1).
/// </para>
/// </summary>
public sealed partial class TaskbarAttentionService : ITaskbarAttention
{
    private const uint FlashwAll = 0x00000003;      // caption + taskbar button
    private const uint FlashwTimerNoFg = 0x0000000C; // keep flashing until the window comes to the foreground

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Flash(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
        {
            return; // window not realised yet (never shown, or already destroyed)
        }

        if (GetForegroundWindow() == handle.Handle)
        {
            return; // the user is already looking at it
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle.Handle,
            Flags = FlashwAll | FlashwTimerNoFg,
            Count = 0, // ignored with FLASHW_TIMERNOFG — it flashes until foreground
            Timeout = 0, // use the system default blink rate
        };

        _ = FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll", EntryPoint = "FlashWindowEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial IntPtr GetForegroundWindow();
}
