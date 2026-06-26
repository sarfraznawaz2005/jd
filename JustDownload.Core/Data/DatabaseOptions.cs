namespace JustDownload.Core.Data;

/// <summary>
/// Tunables for the SQLite data layer. Defaults are chosen for the "light &amp; fast, safe under
/// concurrency" promise: WAL journaling with a busy timeout so a brief writer lock is waited out
/// rather than surfacing as a <c>SQLITE_BUSY</c> error.
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// How long a connection waits for a lock held by another connection before giving up.
    /// Maps to <c>PRAGMA busy_timeout</c>. With WAL this only gates concurrent <em>writers</em>
    /// (readers never block writers), so a few seconds comfortably absorbs normal write bursts.
    /// </summary>
    public TimeSpan BusyTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
