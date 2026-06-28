using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.NativeMessaging;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The Browsers panel (TASK-093): shows whether the native messaging host is registered for each browser and
/// lets the user connect (register) or disconnect (unregister) browser integration. Drives the engine through
/// <see cref="INativeHostInstaller"/> only (§6).
/// </summary>
public sealed partial class BrowsersViewModel : ViewModelBase
{
    private readonly INativeHostInstaller _installer;

    [ObservableProperty]
    private bool _hostAvailable;

    [ObservableProperty]
    private string? _statusMessage;

    public BrowsersViewModel(INativeHostInstaller installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        _installer = installer;
        Refresh();
    }

    /// <summary>Per-browser registration status.</summary>
    public ObservableCollection<BrowserStatusRow> Browsers { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        HostAvailable = _installer.IsHostPresent;
        Browsers.Clear();
        foreach (BrowserRegistrationStatus status in _installer.GetStatus())
        {
            Browsers.Add(new BrowserStatusRow(status.Browser.ToString(), status.IsRegistered));
        }
    }

    [RelayCommand]
    private void Register()
    {
        StatusMessage = _installer.Install()
            ? "Connected — your browsers can now hand downloads to JustDownload."
            : "Couldn't connect: the native host wasn't found next to the app.";
        Refresh();
    }

    [RelayCommand]
    private void Unregister()
    {
        _installer.Uninstall();
        StatusMessage = "Disconnected — browser integration has been removed.";
        Refresh();
    }
}

/// <summary>One browser's connection state in the Browsers panel (TASK-093).</summary>
public sealed record BrowserStatusRow(string Name, bool IsRegistered)
{
    /// <summary>Human-readable status for the row.</summary>
    public string StatusText => IsRegistered ? "Connected" : "Not connected";
}
