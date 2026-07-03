using System.Diagnostics;
using System.Runtime.Versioning;

namespace JustDownload.Core.Updates;

/// <summary>
/// Default <see cref="IUpdateApplier"/> on macOS (TASK-172). The verified asset is a drag-to-Applications
/// <c>.dmg</c> (<c>build/build-macos-packages.ps1</c>) — there is no unattended install step for it, so
/// this mirrors <see cref="WindowsUpdateApplier"/>'s "hand off to the OS, don't silently install" contract:
/// shell-executing the dmg mounts it and opens the volume in Finder (the same as double-clicking it),
/// leaving the actual drag-to-Applications action to the user.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsUpdateApplier : IUpdateApplier
{
    public Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(installerPath);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(installerPath) { UseShellExecute = true };
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to open the update dmg at '{installerPath}'.");
        }

        return Task.CompletedTask;
    }
}
