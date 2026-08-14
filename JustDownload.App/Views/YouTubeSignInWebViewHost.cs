using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using JustDownload.App.Services.YouTube;
using JustDownload.Core.Media.YtDlp;
using Microsoft.Web.WebView2.Core;

namespace JustDownload.App.Views;

/// <summary>
/// Hosts a real WebView2 (Chromium) browser inside the Avalonia visual tree via
/// <see cref="NativeControlHost"/> — the hand-rolled Win32 interop pattern Avalonia's own docs describe
/// for embedding native content, used here instead of the stalled third-party Avalonia WebView2 wrapper
/// (see DECISIONS.md). Navigates to YouTube, watches for a real sign-in round-trip through
/// <c>accounts.google.com</c> and back, then captures every cookie in the session's isolated profile and
/// raises <see cref="SignInCompleted"/> with them as <see cref="NetscapeCookieRecord"/>s — the OS-agnostic
/// shape <c>JustDownload.Core</c> already knows how to store and write to a cookie file. Closes the
/// WebView2 session immediately after capture, per yt-dlp's own documented guidance that keeping the tab
/// open afterward risks the cookie-rotation problem.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class YouTubeSignInWebViewHost : NativeControlHost
{
    private const string InitialUrl = "https://www.youtube.com/";
    private const string GoogleAccountsHost = "accounts.google.com";

    private CoreWebView2Controller? _controller;
    private IntPtr _hwnd;
    private bool _sawGoogleAccounts;
    private bool _completed;

    /// <summary>Raised exactly once per sign-in attempt, whichever way it ends.</summary>
    public event EventHandler<YouTubeSignInCaptureEventArgs>? SignInCompleted;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        try
        {
            IPlatformHandle handle = base.CreateNativeControlCore(parent);
            _hwnd = handle.Handle;
            _ = InitializeWebViewAsync();
            return handle;
        }
        catch (Exception ex) when (ex is COMException or DllNotFoundException or BadImageFormatException
            or PlatformNotSupportedException or InvalidOperationException or TypeLoadException)
        {
            // Platform-level failure (e.g. WebView2 Runtime not installed, architecture mismatch).
            // Raise the failure so the sign-in window receives it, then let the caller handle the throw.
            _completed = true;
            RaiseCompleted(YouTubeSignInOutcome.Failed, cookies: null, DescribeFailure(ex));
            throw;
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _controller?.Close();
        _controller = null;
        base.DestroyNativeControlCore(control);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ResizeController(e.NewSize);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // An app-exclusive profile directory — never the user's real browser profile, and never the
            // same profile across app installs sharing a machine account beyond this app's own data.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Core.AppInfo.Name,
                "webview2-youtube-signin");

            CoreWebView2Environment environment = await CoreWebView2Environment
                .CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder, options: null)
                .ConfigureAwait(true);

            _controller = await environment.CreateCoreWebView2ControllerAsync(_hwnd).ConfigureAwait(true);
            _controller.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            ResizeController(Bounds.Size);
            _controller.IsVisible = true;
            _controller.CoreWebView2.Navigate(InitialUrl);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException
            or DllNotFoundException or BadImageFormatException)
        {
            RaiseCompleted(YouTubeSignInOutcome.Failed, cookies: null, DescribeFailure(ex));
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_completed || !e.IsSuccess || _controller?.CoreWebView2 is not { } core)
        {
            return;
        }

        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        if (uri.Host.Contains(GoogleAccountsHost, StringComparison.OrdinalIgnoreCase))
        {
            // The user reached Google's real sign-in flow — remember it so landing back on youtube.com
            // afterward reads as "signed in", not the page's very first (pre-login) load.
            _sawGoogleAccounts = true;
            return;
        }

        bool onYouTube =
            uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase);

        if (_sawGoogleAccounts && onYouTube)
        {
            _ = CaptureAndCompleteAsync();
        }
    }

    private async Task CaptureAndCompleteAsync()
    {
        if (_completed || _controller?.CoreWebView2 is not { } core)
        {
            return;
        }

        _completed = true;
        try
        {
            // uri: null returns every cookie in this isolated profile (Google/YouTube auth cookies span
            // several subdomains), not just youtube.com's — mirroring what yt-dlp's own
            // --cookies-from-browser reads out of a real browser's cookie jar.
            IReadOnlyList<CoreWebView2Cookie> cookies =
                await core.CookieManager.GetCookiesAsync(null).ConfigureAwait(true);
            NetscapeCookieRecord[] records = cookies.Select(ToRecord).ToArray();

            _controller?.Close();
            _controller = null;

            RaiseCompleted(YouTubeSignInOutcome.Succeeded, records, errorMessage: null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            RaiseCompleted(YouTubeSignInOutcome.Failed, cookies: null, $"Couldn't read the signed-in session: {ex.Message}");
        }
    }

    private static NetscapeCookieRecord ToRecord(CoreWebView2Cookie cookie) => new(
        Domain: cookie.Domain,
        IncludeSubdomains: cookie.Domain.StartsWith('.'),
        Path: cookie.Path,
        Secure: cookie.IsSecure,
        ExpiresUnixSeconds: cookie.IsSession ? 0 : ExpiresUnixSeconds(cookie.Expires),
        Name: cookie.Name,
        Value: cookie.Value);

    // CoreWebView2Cookie.Expires is a DateTime (not already-UTC-tagged — treat it as UTC, which is what
    // WebView2 actually reports); clamp to 0 so a stray pre-epoch value can never write a negative expiry.
    private static long ExpiresUnixSeconds(DateTime expires) =>
        Math.Max(0, new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)).ToUnixTimeSeconds());

    private void RaiseCompleted(YouTubeSignInOutcome outcome, IReadOnlyList<NetscapeCookieRecord>? cookies, string? errorMessage) =>
        SignInCompleted?.Invoke(this, new YouTubeSignInCaptureEventArgs(outcome, cookies, errorMessage));

    // Sized from Avalonia's own layout (the control's Bounds/NewSize, scaled by RenderScaling to physical
    // pixels) rather than a native GetClientRect call against _hwnd: the native "dumb window" handle is
    // captured once at creation and GetClientRect against it raced Avalonia's own resize/maximize layout
    // pass, leaving the WebView2 controller's Bounds pinned at the window's original size.
    private void ResizeController(Size sizeInDips)
    {
        if (_controller is null)
        {
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = Math.Max(0, (int)Math.Round(sizeInDips.Width * scaling));
        int height = Math.Max(0, (int)Math.Round(sizeInDips.Height * scaling));
        _controller.Bounds = new System.Drawing.Rectangle(0, 0, width, height);
    }

    private static string DescribeFailure(Exception ex) =>
        $"Couldn't start the embedded browser (the WebView2 Runtime may be missing): {ex.Message}";
}
