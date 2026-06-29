using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The add-media / quality picker dialog (TASK-060/100): a thin shell over
/// <see cref="MediaVariantPickerViewModel"/>. Paste a URL, it extracts the available qualities, and Download
/// enqueues the chosen variant as a media download. The view-model owns all behaviour; this code-behind only
/// triggers extraction on URL commit and closes when the view-model asks.
/// </summary>
public partial class MediaVariantPickerWindow : Window
{
    public MediaVariantPickerWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => UrlBox.Focus();
        UrlBox.LostFocus += OnUrlCommitted;
        UrlBox.KeyDown += OnUrlKeyDown;
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TriggerDetect();
        }
    }

    private void OnUrlCommitted(object? sender, RoutedEventArgs e) => TriggerDetect();

    private void TriggerDetect()
    {
        if (DataContext is MediaVariantPickerViewModel { CanDetect: true } vm)
        {
            _ = vm.DetectAsync();
        }
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MediaVariantPickerViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool enqueued) => Close(enqueued);

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaVariantPickerViewModel vm)
        {
            await vm.ConfirmAsync();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
