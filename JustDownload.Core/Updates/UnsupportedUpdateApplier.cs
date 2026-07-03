namespace JustDownload.Core.Updates;

/// <summary>
/// Registered on any OS other than Windows/macOS/Linux (TASK-172 — those three now have a real applier).
/// <see cref="UpdateChecker.ResolveInstallerAssetName"/> never resolves an asset name off those three OSes,
/// so <see cref="UpdateChecker"/> never reaches the point of invoking this; it exists only so dependency
/// injection always has an <see cref="IUpdateApplier"/> to resolve.
/// </summary>
internal sealed class UnsupportedUpdateApplier : IUpdateApplier
{
    public Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("Applying an update automatically is only supported on Windows, macOS, and Linux.");
}
