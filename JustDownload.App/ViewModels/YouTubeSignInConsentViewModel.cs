using CommunityToolkit.Mvvm.Input;

namespace JustDownload.App.ViewModels;

/// <summary>The user's choice on the one-time "Sign in to YouTube" consent notice.</summary>
public enum YouTubeSignInConsentResult
{
    Cancel,
    Continue,
}

/// <summary>
/// The one-time consent/ban-risk notice shown before the "Sign in to YouTube" modal's first use (Windows
/// only). Mirrors <see cref="TosNoticeViewModel"/>'s pattern: the copy lives here so the view stays dumb,
/// and the gate that decides whether to show it at all
/// (<see cref="Services.IYouTubeSignInConsentGate"/>) stays unit-testable without a live window.
/// </summary>
public sealed partial class YouTubeSignInConsentViewModel : ViewModelBase
{
    public string Heading { get; } = "Before you sign in to YouTube";

    public string Intro { get; } =
        "JustDownload can open an embedded, app-exclusive browser session so yt-dlp can download videos "
        + "that require a signed-in account. This session is separate from your regular browser — "
        + "JustDownload never reads your browser's own cookies.";

    public IReadOnlyList<string> Bullets { get; } =
    [
        "Your Google account's session cookies are captured once and stored only in this Windows "
            + "account's OS-protected credential vault (DPAPI) — never as plain text.",
        "Automated, account-linked access carries a real risk your Google account could be flagged or "
            + "temporarily limited by YouTube, per yt-dlp's own documented warning. Only continue if you "
            + "accept that risk.",
        "You can revoke this session at any time from Settings → Video → Sign out.",
    ];

    public string Confirmation { get; } = "By continuing, you confirm that you understand this and accept the risk.";

    public event EventHandler<YouTubeSignInConsentResult>? CloseRequested;

    [RelayCommand]
    private void Continue() => CloseRequested?.Invoke(this, YouTubeSignInConsentResult.Continue);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, YouTubeSignInConsentResult.Cancel);
}
