using System.Globalization;

namespace JustDownload.Core.Lifecycle;

/// <summary>
/// Maps <see cref="DownloadStatus"/> to and from the stable string codes persisted in the
/// <c>downloads.status</c> column (TASK-031). Keeping the persisted vocabulary in one place means the
/// repository stores opaque strings (it never interprets them) while the engine works in the strongly
/// typed enum. Codes are lower-case and deliberately decoupled from the enum member names so renaming a
/// member never silently changes the on-disk format.
/// </summary>
public static class DownloadStatusCodes
{
    /// <summary><see cref="DownloadStatus.Queued"/>.</summary>
    public const string Queued = "queued";

    /// <summary><see cref="DownloadStatus.Active"/>.</summary>
    public const string Active = "active";

    /// <summary><see cref="DownloadStatus.Paused"/>.</summary>
    public const string Paused = "paused";

    /// <summary><see cref="DownloadStatus.Completed"/>.</summary>
    public const string Completed = "complete";

    /// <summary><see cref="DownloadStatus.Failed"/>.</summary>
    public const string Failed = "error";

    /// <summary><see cref="DownloadStatus.Expired"/>.</summary>
    public const string Expired = "expired";

    /// <summary>Returns the persisted code for <paramref name="status"/>.</summary>
    public static string ToCode(DownloadStatus status) => status switch
    {
        DownloadStatus.Queued => Queued,
        DownloadStatus.Active => Active,
        DownloadStatus.Paused => Paused,
        DownloadStatus.Completed => Completed,
        DownloadStatus.Failed => Failed,
        DownloadStatus.Expired => Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown download status."),
    };

    /// <summary>Parses a persisted status code (case-insensitive) into a <see cref="DownloadStatus"/>.</summary>
    /// <exception cref="FormatException">The code is not a recognized status.</exception>
    public static DownloadStatus Parse(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return code.ToLowerInvariant() switch
        {
            Queued => DownloadStatus.Queued,
            Active => DownloadStatus.Active,
            Paused => DownloadStatus.Paused,
            Completed => DownloadStatus.Completed,
            Failed => DownloadStatus.Failed,
            Expired => DownloadStatus.Expired,
            _ => throw new FormatException(
                string.Create(CultureInfo.InvariantCulture, $"'{code}' is not a recognized download status code.")),
        };
    }
}
