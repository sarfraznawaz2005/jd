using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using JustDownload.App.ViewModels;

namespace JustDownload.App.Views;

/// <summary>
/// The New URL dialog window (TASK-053): a thin shell over <see cref="NewDownloadViewModel"/>. The view-model
/// owns all behaviour and validation; this code-behind only closes the window when the view-model asks and
/// runs the OS folder picker for "Browse" (a top-level concern that cannot live in the view-model).
/// </summary>
public partial class NewDownloadWindow : Window
{
    // Debounce so detection doesn't fire on every keystroke while typing/pasting (TASK-184) — waits for a
    // short pause instead. Previously detection only fired on blur/Enter, which read as "nothing happens"
    // until you clicked away (user-reported).
    private static readonly TimeSpan AutoDetectDebounce = TimeSpan.FromMilliseconds(500);
    private readonly DispatcherTimer _autoDetectTimer;

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

        _autoDetectTimer = new DispatcherTimer { Interval = AutoDetectDebounce };
        _autoDetectTimer.Tick += (_, _) =>
        {
            _autoDetectTimer.Stop();
            TriggerDetect();
        };
        Closed += (_, _) => _autoDetectTimer.Stop();
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _autoDetectTimer.Stop();
            TriggerDetect();
        }
    }

    private void OnUrlCommitted(object? sender, RoutedEventArgs e)
    {
        _autoDetectTimer.Stop();
        TriggerDetect();
    }

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
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Restarts the debounce on every Url edit — typing/pasting, not just losing focus (TASK-184).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NewDownloadViewModel.Url))
        {
            _autoDetectTimer.Stop();
            _autoDetectTimer.Start();
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
