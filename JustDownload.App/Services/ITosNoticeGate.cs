namespace JustDownload.App.Services;

/// <summary>
/// Gates media extraction on the one-time "may violate site ToS" notice (docs/LEGAL.md, CLAUDE.md §5,
/// TASK-160). Consulted right before extraction actually runs — never before plain HTTP/FTP downloads.
/// </summary>
public interface ITosNoticeGate
{
    /// <summary>
    /// Shows the notice unless it has already been suppressed, and waits for the user's choice.
    /// </summary>
    /// <returns><see langword="true"/> if extraction may proceed; <see langword="false"/> if the user canceled.</returns>
    Task<bool> ConfirmAsync(CancellationToken cancellationToken = default);
}
