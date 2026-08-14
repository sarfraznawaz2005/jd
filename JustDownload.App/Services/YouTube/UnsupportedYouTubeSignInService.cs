using Avalonia.Controls;

namespace JustDownload.App.Services.YouTube;

/// <summary>
/// Fallback <see cref="IYouTubeSignInService"/> for macOS/Linux, where WebView2 has no equivalent in v1's
/// scope. <see cref="IsSupported"/> is <see langword="false"/> so the Settings UI hides the "Sign in to
/// YouTube" button behind the documented "use the cookie-file option instead" message and never actually
/// calls <see cref="SignInAsync"/> — the exception here is a defensive fallback, not the expected path.
/// </summary>
internal sealed class UnsupportedYouTubeSignInService : IYouTubeSignInService
{
    public bool IsSupported => false;

    public Task<YouTubeSignInResult> SignInAsync(Window owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Task.FromResult(new YouTubeSignInResult(
            YouTubeSignInOutcome.Failed,
            "Browser sign-in isn't available on this OS yet — use the cookie-file option instead."));
    }
}
