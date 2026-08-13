namespace JustDownload.App.Services;

/// <summary>
/// Gates media downloads on the one-time "may violate site ToS" notice (docs/LEGAL.md, CLAUDE.md §5,
/// TASK-160). Consulted at the point the user commits to media: either right before extraction runs
/// (<see cref="JustDownload.App.ViewModels.MediaVariantPickerViewModel"/>, where the user deliberately opened
/// the picker) or right before an extraction-backed download is enqueued
/// (<see cref="JustDownload.App.ViewModels.NewDownloadViewModel"/>, whose detection runs automatically while
/// typing and must not raise a modal there). Never consulted for plain HTTP/FTP downloads.
/// </summary>
public interface ITosNoticeGate
{
    /// <summary>
    /// Shows the notice unless it has already been suppressed, and waits for the user's choice.
    /// </summary>
    /// <returns><see langword="true"/> if extraction may proceed; <see langword="false"/> if the user canceled.</returns>
    Task<bool> ConfirmAsync(CancellationToken cancellationToken = default);
}
