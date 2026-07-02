using Avalonia.Controls;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The one-time ToS/legal notice dialog (TASK-160): a thin shell over <see cref="TosNoticeViewModel"/>. The
/// view-model owns the copy and the three actions; this code-behind only closes the window with the chosen
/// <see cref="TosNoticeResult"/> so the caller's <c>ShowDialog&lt;TosNoticeResult&gt;</c> resolves with it.
/// </summary>
public partial class TosNoticeWindow : Window
{
    public TosNoticeWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is TosNoticeViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, TosNoticeResult result) => Close(result);
}
