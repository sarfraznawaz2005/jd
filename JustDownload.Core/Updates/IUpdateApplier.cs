namespace JustDownload.Core.Updates;

/// <summary>
/// Launches an already-verified update installer (TASK-080, locked scope: no in-place self-replacement,
/// no relauncher helper — download, verify, launch, then the caller exits the app). Tests substitute a
/// fake so no real process is spawned (CLAUDE.md §3).
/// </summary>
public interface IUpdateApplier
{
    /// <summary>
    /// Launches the already-verified installer at <paramref name="installerPath"/>. Does not itself exit the
    /// app — the caller (the Settings UI) does that once this returns, so the installer isn't racing a
    /// half-torn-down process.
    /// </summary>
    Task ApplyAsync(string installerPath, CancellationToken cancellationToken = default);
}
