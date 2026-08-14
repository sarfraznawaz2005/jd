using Avalonia.Controls;

namespace JustDownload.App.Services.YouTube;

/// <summary>
/// Opens the "Sign in to YouTube" modal (Windows only, WebView2 — D1 one-time exception) and, on success,
/// hands the captured session to <see cref="JustDownload.Core.Media.YtDlp.IYouTubeSessionStore"/>. Not
/// implemented for macOS/Linux in v1 (<see cref="IsSupported"/> is <see langword="false"/> there); Settings
/// hides the button behind a documented fallback message instead of calling <see cref="SignInAsync"/>.
/// </summary>
public interface IYouTubeSignInService
{
    /// <summary>Whether this OS can host the sign-in modal at all.</summary>
    bool IsSupported { get; }

    /// <summary>Shows the modal over <paramref name="owner"/> and waits for the user to finish or cancel.</summary>
    Task<YouTubeSignInResult> SignInAsync(Window owner, CancellationToken cancellationToken = default);

    /// <summary>Clears any saved profile data for the sign-in session.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
