namespace JustDownload.App.Services;

/// <summary>
/// Gates the "Sign in to YouTube" modal on the one-time consent/ban-risk notice. Mirrors
/// <see cref="ITosNoticeGate"/>'s pattern exactly.
/// </summary>
public interface IYouTubeSignInConsentGate
{
    /// <summary>Shows the notice unless it has already been acknowledged, and waits for the user's choice.</summary>
    /// <returns><see langword="true"/> if the sign-in modal may proceed; <see langword="false"/> if the user canceled.</returns>
    Task<bool> ConfirmAsync(CancellationToken cancellationToken = default);
}
