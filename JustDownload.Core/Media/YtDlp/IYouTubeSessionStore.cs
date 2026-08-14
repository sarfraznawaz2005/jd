namespace JustDownload.Core.Media.YtDlp;

/// <summary>
/// Owns the app-exclusive "Sign in to YouTube" session (Windows only, JustDownload.App's WebView2 sign-in
/// modal — D1 one-time exception, CLAUDE.md §5 "secrets at rest"). The captured cookies are a credential:
/// the plaintext lives only in the OS secret vault (<see cref="Security.ISecretStore"/>), and this type is
/// the only thing that ever materializes them to disk — a single, deterministic temp file that
/// <see cref="Settings.AppSettings.YtDlpCookieFilePath"/> is pointed at so <c>YtDlpMediaExtractor</c>
/// consumes it through its existing <c>--cookies &lt;path&gt;</c> path with no changes of its own.
/// </summary>
public interface IYouTubeSessionStore
{
    /// <summary>Whether a session is currently stored, from the cached settings snapshot (no I/O).</summary>
    bool HasSession { get; }

    /// <summary>
    /// Replaces any existing session with <paramref name="cookies"/>: stores them in the OS secret vault,
    /// materializes the Netscape-format temp file, and points <c>YtDlpCookieFilePath</c> at it.
    /// </summary>
    Task StoreAsync(IReadOnlyList<NetscapeCookieRecord> cookies, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored session: the secret vault entry, the materialized temp file, and clears both
    /// settings fields. Safe to call when no session is stored.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-materializes the temp file from the OS secret vault if a session is stored but the file is
    /// missing (e.g. the OS swept the temp directory since the last run). Call once at app startup, before
    /// any extraction can run. A no-op when there is no session, the file already exists, or the vault
    /// entry itself has vanished (in which case the dangling session reference is cleared).
    /// </summary>
    Task EnsureMaterializedAsync(CancellationToken cancellationToken = default);
}
