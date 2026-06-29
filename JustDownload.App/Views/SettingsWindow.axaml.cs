using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using JustDownload.App.ViewModels.Settings;

namespace JustDownload.App.Views;

/// <summary>The settings window (TASK-057): a section nav rail and the selected section's content.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    /// <summary>
    /// Runs the OS folder picker for the General section's default-download-folder "Browse" (TASK-121); a
    /// top-level concern that cannot live in the view-model. The button's data context is the section VM.
    /// </summary>
    private async void OnBrowseDefaultFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GeneralSettingsViewModel vm)
        {
            return;
        }

        var options = new FolderPickerOpenOptions { Title = "Choose default download folder", AllowMultiple = false };
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(options);
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.DefaultDownloadFolder = path;
        }
    }

    private static readonly FilePickerFileType SettingsFileType =
        new("JustDownload settings") { Patterns = ["*.json"] };

    /// <summary>Picks a destination file and exports the current settings to it (TASK-129).</summary>
    private async void OnExportSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings",
            SuggestedFileName = "justdownload-settings.json",
            DefaultExtension = "json",
            FileTypeChoices = [SettingsFileType],
        });

        if (file?.TryGetLocalPath() is { } path)
        {
            await vm.ExportToAsync(path);
        }
    }

    /// <summary>Picks a settings export file and imports it, restoring the saved preferences (TASK-129).</summary>
    private async void OnImportSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import settings",
            AllowMultiple = false,
            FileTypeFilter = [SettingsFileType],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await vm.ImportFromAsync(path);
        }
    }
}
