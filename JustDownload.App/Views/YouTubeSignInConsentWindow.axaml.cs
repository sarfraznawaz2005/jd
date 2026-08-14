using Avalonia.Controls;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The one-time "Sign in to YouTube" consent notice dialog: a thin shell over
/// <see cref="YouTubeSignInConsentViewModel"/>, mirroring <see cref="TosNoticeWindow"/>'s pattern.
/// </summary>
public partial class YouTubeSignInConsentWindow : Window
{
    public YouTubeSignInConsentWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is YouTubeSignInConsentViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, YouTubeSignInConsentResult result) => Close(result);
}
