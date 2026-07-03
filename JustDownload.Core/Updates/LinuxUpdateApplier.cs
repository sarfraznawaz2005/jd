using System.Diagnostics;
using System.Runtime.Versioning;

namespace JustDownload.Core.Updates;

/// <summary>
/// Default <see cref="IUpdateApplier"/> on Linux (TASK-172). The verified asset is the AppImage
/// (<c>build/build-linux-packages.ps1</c>) — unlike Windows/macOS there's no reliable desktop-wide
/// "open with the default handler" for it (<c>xdg-open</c> has no consistent AppImage association across
/// distros/desktop environments), so this sets the executable bit (downloaded files aren't executable by
/// default) and launches it directly as a process instead of shell-executing it. Running the AppImage *is*
/// launching the new version — the caller (Settings UI) exits the old one once this returns, per
/// <see cref="IUpdateApplier"/>'s contract.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxUpdateApplier : IUpdateApplier
{
    public Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(installerPath);
        cancellationToken.ThrowIfCancellationRequested();

        UnixFileMode mode = File.GetUnixFileMode(installerPath);
        File.SetUnixFileMode(
            installerPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        var startInfo = new ProcessStartInfo(installerPath) { UseShellExecute = false };
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to launch the update AppImage at '{installerPath}'.");
        }

        return Task.CompletedTask;
    }
}
