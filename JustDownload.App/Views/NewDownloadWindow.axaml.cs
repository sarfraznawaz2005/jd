using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The New URL dialog window (TASK-053): a thin shell over <see cref="NewDownloadViewModel"/>. The view-model
/// owns all behaviour and validation; this code-behind only closes the window when the view-model asks and
/// runs the OS folder picker for "Browse" (a top-level concern that cannot live in the view-model).
/// </summary>
public partial class NewDownloadWindow : Window
{
    public NewDownloadWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Focus the URL field once the window is actually shown (focusing before it is attached is invalid).
        Opened += (_, _) => UrlBox.Focus();

        // Auto-detect after the user finishes entering/pasting the URL (on blur), keeping the trigger in the
        // view so the view-model's property setters stay side-effect-free and testable.
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
        if (DataContext is NewDownloadViewModel { CanDetect: true } vm)
        {
            _ = vm.DetectAsync();
        }
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is NewDownloadViewModel vm)
        {
            vm.CloseRequested -= OnCloseRequested;
            vm.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool enqueued) => Close(enqueued);

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not NewDownloadViewModel vm)
        {
            return;
        }

        IStorageProvider storage = StorageProvider;
        var options = new FolderPickerOpenOptions { Title = "Choose download folder", AllowMultiple = false };

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.SaveToFolder = path;
        }
    }
}
