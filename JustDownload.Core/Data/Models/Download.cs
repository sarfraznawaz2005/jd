namespace JustDownload.Core.Data.Models;

/// <summary>
/// A persisted download row (PRD §4.4 <c>downloads</c> table). This is the durable record the
/// resume feature is built on: the queue, per-download limits, server validators (ETag /
/// Last-Modified) and lifecycle timestamps all survive a restart through this entity.
/// <para>
/// Status and category fields are stored as their string codes to keep the schema flexible while
/// the engine's status vocabulary evolves; the repository never interprets them, it only persists
/// and returns them verbatim. Credentials are <b>never</b> part of this record (CLAUDE.md §5).
/// </para>
/// </summary>
public sealed record Download
{
    /// <summary>The auto-increment primary key. <c>0</c> for a not-yet-inserted record.</summary>
    public long Id { get; init; }

    /// <summary>The source URL to download from. Required.</summary>
    public required string Url { get; init; }

    /// <summary>The referring page URL, used for renew flows and authenticated fetches.</summary>
    public string? Referrer { get; init; }

    /// <summary>The target file name (often derived from <c>Content-Disposition</c>).</summary>
    public string? Filename { get; init; }

    /// <summary>The target directory the file is written to.</summary>
    public string? Directory { get; init; }

    /// <summary>Total size in bytes when known; <see langword="null"/> for unknown-length sources.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The lifecycle status code (e.g. <c>queued</c>, <c>downloading</c>, <c>complete</c>).</summary>
    public required string Status { get; init; }

    /// <summary>The type category code (Video, Audio, Document, …) for auto-organization.</summary>
    public string? CategoryType { get; init; }

    /// <summary>The status category code (Complete / Incomplete) for auto-organization.</summary>
    public string? CategoryStatus { get; init; }

    /// <summary>The server's <c>ETag</c> validator, used to confirm resume identity.</summary>
    public string? ETag { get; init; }

    /// <summary>The server's <c>Last-Modified</c> validator, stored verbatim as the HTTP header value.</summary>
    public string? LastModified { get; init; }

    /// <summary>When the download was created (UTC).</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the download finished (UTC); <see langword="null"/> while incomplete.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The last error message, if the download failed.</summary>
    public string? Error { get; init; }

    /// <summary>Per-download maximum connection count override; <see langword="null"/> uses the global default.</summary>
    public int? MaxConnections { get; init; }

    /// <summary>Per-download speed cap in bytes/sec; <see langword="null"/> or <c>0</c> means unlimited.</summary>
    public long? SpeedLimit { get; init; }

    /// <summary>
    /// Queue priority (TASK-072, US-16): higher runs sooner; ties break on <see cref="CreatedAt"/>. Default
    /// <c>0</c>. The drag-to-reorder UI persists new values here so the order survives a restart.
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Opaque <c>secret_ref</c> (TASK-091, §5) for the request cookies captured by the browser extension, if
    /// any. The cookies themselves live only in the OS keychain; this reference is the <b>only</b> thing
    /// persisted here and discloses nothing on its own. Resolved on download/resume to send a <c>Cookie</c>
    /// header so authenticated/signed media downloads succeed.
    /// </summary>
    public string? CookieSecretRef { get; init; }

    /// <summary>
    /// How many times the engine has auto-retried a transient failure for this download (TASK-131).
    /// Persisted so the count survives restarts and is visible to the UI; <c>0</c> for a download that
    /// has never been retried.
    /// </summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// Per-download proxy override kind (TASK-153), stored as the integer value of
    /// <c>JustDownload.Core.Transport.Proxy.ProxyKind</c>. <see langword="null"/> means "no override — use the
    /// global proxy". The remaining <c>Proxy*</c> fields are meaningful only when this is set.
    /// </summary>
    public int? ProxyKind { get; init; }

    /// <summary>The override proxy host (TASK-153), or <see langword="null"/>.</summary>
    public string? ProxyHost { get; init; }

    /// <summary>The override proxy port (TASK-153), or <see langword="null"/>.</summary>
    public int? ProxyPort { get; init; }

    /// <summary>The override proxy auth user name (TASK-153), or <see langword="null"/> for no auth.</summary>
    public string? ProxyUsername { get; init; }

    /// <summary>The override proxy NTLM/Negotiate domain (TASK-153), or <see langword="null"/>.</summary>
    public string? ProxyDomain { get; init; }

    /// <summary>
    /// Opaque OS-keychain reference (§5) for the override proxy password — never the password itself
    /// (TASK-153). Resolved on start/resume when building the per-download proxy. <see langword="null"/> when
    /// the override has no password.
    /// </summary>
    public string? ProxyPasswordSecretRef { get; init; }
}
