using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The standalone per-download progress window (TASK-225, mockups/detail.html). A thin shell: it hosts the
/// shared <see cref="Views.DownloadDetailView"/> content and only translates the view-model's close intent
/// into an actual window close, disposing the view-model's timers/subscriptions on the way out.
/// </summary>
public partial class DownloadProgressWindow : Window
{
    public DownloadProgressWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is DownloadProgressViewModel viewModel)
        {
            viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is DownloadProgressViewModel viewModel)
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Dispose();
        }
    }
}
