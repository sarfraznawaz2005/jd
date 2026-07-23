using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The standalone per-download progress window (TASK-225, mockups/detail.html). A thin shell: it hosts the
/// shared <see cref="Views.DownloadDetailView"/> content and only translates the view-model's close intent
/// into an actual window close, disposing the view-model's timers/subscriptions on the way out. It also owns
/// the one bit of layout the view-model can't express in XAML alone: snapping the window's height to fit
/// whichever of the full/compact views <see cref="DownloadProgressViewModel.IsCompact"/> selects.
/// </summary>
public partial class DownloadProgressWindow : Window
{
    private const double FullHeight = 580;
    private const double FullMinHeight = 420;

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
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadProgressViewModel.IsCompact)
            && DataContext is DownloadProgressViewModel viewModel)
        {
            ApplyLayoutMode(viewModel.IsCompact);
        }
    }

    /// <summary>
    /// Snaps the window to fit whichever view is now showing. Compact lets the content dictate its own
    /// (small) height and locks resizing — there is nothing to resize into; expanding restores the standard
    /// fixed size rather than whatever the user had last dragged it to, keeping the snap deterministic.
    /// </summary>
    private void ApplyLayoutMode(bool isCompact)
    {
        if (isCompact)
        {
            CanResize = false;
            MinHeight = 0;
            SizeToContent = SizeToContent.Height;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            MinHeight = FullMinHeight;
            Height = FullHeight;
            CanResize = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is DownloadProgressViewModel viewModel)
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        }
    }
}
