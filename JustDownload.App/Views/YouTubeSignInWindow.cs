using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using JustDownload.App.Services.YouTube;
using JustDownload.Core.Media.YtDlp;

namespace JustDownload.App.Views;

/// <summary>
/// The "Sign in to YouTube" modal (Windows only): a thin shell around <see cref="YouTubeSignInWebViewHost"/>.
/// Built entirely in code, not AXAML — it references <c>Microsoft.Web.WebView2</c> types, which do not exist
/// on macOS/Linux builds (JustDownload.App.csproj excludes this file's compilation there), and an AXAML file
/// referencing this control's CLR type would need the same exclusion on the XAML-compiler side too. Plain
/// code sidesteps that entirely with one file, one condition.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class YouTubeSignInWindow : Window
{
    private readonly YouTubeSignInWebViewHost _host = new();

    public YouTubeSignInWindow()
    {
        Title = "Sign in to YouTube";
        Width = 480;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var instructions = new TextBlock
        {
            Text = "Sign in with your Google account below. This session is app-exclusive — JustDownload " +
                "never reads your regular browser's cookies. Close this window to cancel.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(12, 12, 12, 8),
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(instructions, 0);
        Grid.SetRow(_host, 1);
        grid.Children.Add(instructions);
        grid.Children.Add(_host);
        Content = grid;

        _host.SignInCompleted += OnSignInCompleted;
    }

    /// <summary>The captured session on success; <see langword="null"/> otherwise.</summary>
    public IReadOnlyList<NetscapeCookieRecord>? CapturedCookies { get; private set; }

    /// <summary>A user-facing description when the attempt failed.</summary>
    public string? ErrorMessage { get; private set; }

    private void OnSignInCompleted(object? sender, YouTubeSignInCaptureEventArgs e)
    {
        CapturedCookies = e.Cookies;
        ErrorMessage = e.ErrorMessage;
        Close(e.Outcome);
    }
}
