using System.Diagnostics;
using System.Runtime.Versioning;

namespace JustDownload.Core.Updates;

/// <summary>
/// Default <see cref="IUpdateApplier"/> on Windows (TASK-080, locked scope — apply is Windows-only for now).
/// Launches the verified installer with shell execute (so Windows shows its own UAC/setup UI) and returns;
/// it does not wait for the installer to finish or exit the app itself.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUpdateApplier : IUpdateApplier
{
    public Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(installerPath);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(installerPath) { UseShellExecute = true };
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to launch the update installer at '{installerPath}'.");
        }

        return Task.CompletedTask;
    }
}
