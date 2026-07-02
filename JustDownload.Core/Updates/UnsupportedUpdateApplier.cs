namespace JustDownload.Core.Updates;

/// <summary>
/// Registered on macOS/Linux, where auto-apply isn't built yet (TASK-080 locked scope — apply is
/// Windows-only until TASK-077/078's packaging lands). <see cref="UpdateChecker"/> never invokes this off
/// Windows; it exists only so dependency injection always has an <see cref="IUpdateApplier"/> to resolve.
/// </summary>
internal sealed class UnsupportedUpdateApplier : IUpdateApplier
{
    public Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("Applying an update automatically is only supported on Windows.");
}
