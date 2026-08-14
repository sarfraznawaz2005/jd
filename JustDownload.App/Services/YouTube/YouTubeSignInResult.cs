namespace JustDownload.App.Services.YouTube;

/// <summary>
/// How a "Sign in to YouTube" attempt ended. <see cref="Cancelled"/> is deliberately the first (default)
/// member: <c>Window.ShowDialog&lt;T&gt;</c> returns <see langword="default"/> when the user closes the
/// modal via its title-bar X instead of a button, and that must read as "cancelled", not "succeeded".
/// </summary>
public enum YouTubeSignInOutcome
{
    Cancelled = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>The outcome of <see cref="IYouTubeSignInService.SignInAsync"/>.</summary>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="ErrorMessage">A user-facing description when <paramref name="Outcome"/> is <see cref="YouTubeSignInOutcome.Failed"/>.</param>
public sealed record YouTubeSignInResult(YouTubeSignInOutcome Outcome, string? ErrorMessage = null);
