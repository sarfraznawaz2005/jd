using System.Runtime.Versioning;
using JustDownload.App.Services.YouTube;
using JustDownload.Core.Media.YtDlp;

namespace JustDownload.App.Views;

/// <summary>Raised by <see cref="YouTubeSignInWebViewHost"/> once the embedded session finishes, one way or another.</summary>
[SupportedOSPlatform("windows")]
internal sealed class YouTubeSignInCaptureEventArgs : EventArgs
{
    public YouTubeSignInCaptureEventArgs(
        YouTubeSignInOutcome outcome, IReadOnlyList<NetscapeCookieRecord>? cookies, string? errorMessage)
    {
        Outcome = outcome;
        Cookies = cookies;
        ErrorMessage = errorMessage;
    }

    public YouTubeSignInOutcome Outcome { get; }

    public IReadOnlyList<NetscapeCookieRecord>? Cookies { get; }

    public string? ErrorMessage { get; }
}
