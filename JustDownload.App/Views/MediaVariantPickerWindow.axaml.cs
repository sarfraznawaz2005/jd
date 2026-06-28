using Avalonia.Controls;
using Avalonia.Interactivity;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The add-video / quality picker dialog (TASK-060). A thin shell over
/// <see cref="MediaVariantPickerViewModel"/>: it shows the detected qualities and closes with the chosen
/// quality + container, or <see langword="null"/> on cancel.
/// </summary>
public partial class MediaVariantPickerWindow : Window
{
    public MediaVariantPickerWindow() => InitializeComponent();

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaVariantPickerViewModel { SelectedVariant: not null } vm)
        {
            Close(new MediaVariantSelection(vm.SelectedVariant.Variant, vm.SelectedAudio?.Variant, vm.SelectedContainer));
        }
        else
        {
            Close(null);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
