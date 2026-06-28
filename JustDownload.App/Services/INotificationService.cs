namespace JustDownload.App.Services;

/// <summary>The flavour of a user notification (TASK-061), driving its icon/colour.</summary>
public enum AppNotificationKind
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>A successful outcome (e.g. a download completed).</summary>
    Success,

    /// <summary>A failure (e.g. a download errored).</summary>
    Error,
}

/// <summary>A notification to surface to the user (TASK-061).</summary>
/// <param name="Title">The short headline.</param>
/// <param name="Message">The body text.</param>
/// <param name="Kind">The notification flavour.</param>
public sealed record AppNotification(string Title, string Message, AppNotificationKind Kind);

/// <summary>
/// Surfaces a user-facing notification (TASK-061 AC0). The default implementation shows an in-app toast; it
/// is an interface so the download notifier (and tests) depend only on the contract.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows <paramref name="notification"/> to the user.</summary>
    void Notify(AppNotification notification);
}
