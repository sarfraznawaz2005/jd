using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using JustDownload.App.Views;
using JustDownload.Core.Media.YtDlp;

namespace JustDownload.App.Services.YouTube;

/// <summary>
/// Real <see cref="IYouTubeSignInService"/> (Windows only): shows <see cref="YouTubeSignInWindow"/> as a
/// dialog and, on a successful capture, hands the cookies straight to <see cref="IYouTubeSessionStore"/> —
/// callers only ever see the pass/fail/cancel outcome, never the raw session.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsYouTubeSignInService : IYouTubeSignInService
{
    private readonly IYouTubeSessionStore _sessionStore;

    public WindowsYouTubeSignInService(IYouTubeSessionStore sessionStore)
    {
        ArgumentNullException.ThrowIfNull(sessionStore);
        _sessionStore = sessionStore;
    }

    public bool IsSupported => true;

    public async Task<YouTubeSignInResult> SignInAsync(Window owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        YouTubeSignInWindow window;
        try
        {
            window = new YouTubeSignInWindow();
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or BadImageFormatException
            or PlatformNotSupportedException or InvalidOperationException or TypeLoadException)
        {
            return new YouTubeSignInResult(
                YouTubeSignInOutcome.Failed,
                $"Couldn't start the embedded browser (the WebView2 Runtime may be missing): {ex.Message}");
        }

        try
        {
            YouTubeSignInOutcome outcome = await window.ShowDialog<YouTubeSignInOutcome>(owner).ConfigureAwait(true);

            if (outcome == YouTubeSignInOutcome.Cancelled)
            {
                return new YouTubeSignInResult(YouTubeSignInOutcome.Cancelled);
            }

            if (outcome == YouTubeSignInOutcome.Failed || window.CapturedCookies is not { Count: > 0 } cookies)
            {
                return new YouTubeSignInResult(
                    YouTubeSignInOutcome.Failed, window.ErrorMessage ?? "Sign-in did not complete.");
            }

            await _sessionStore.StoreAsync(cookies, cancellationToken).ConfigureAwait(true);
            return new YouTubeSignInResult(YouTubeSignInOutcome.Succeeded);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            return new YouTubeSignInResult(
                YouTubeSignInOutcome.Failed,
                $"The embedded browser encountered an error: {ex.Message}");
        }
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Core.AppInfo.Name,
            "webview2-youtube-signin");

        try
        {
            if (Directory.Exists(userDataFolder))
            {
                Directory.Delete(userDataFolder, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore if the folder is locked or cannot be deleted
        }

        return Task.CompletedTask;
    }
}
