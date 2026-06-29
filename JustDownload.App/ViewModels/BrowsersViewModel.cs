using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core;
using JustDownload.Core.NativeMessaging;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The Browsers panel (TASK-093): shows whether the native messaging host is registered for each browser and
/// lets the user connect (register) or disconnect (unregister) browser integration. Drives the engine through
/// <see cref="INativeHostInstaller"/> only (§6). In portable mode (TASK-138) registration is disabled because
/// it would write the host manifest into the registry — which portable mode must never do.
/// </summary>
public sealed partial class BrowsersViewModel : ViewModelBase
{
    private readonly INativeHostInstaller _installer;

    [ObservableProperty]
    private bool _hostAvailable;

    [ObservableProperty]
    private string? _statusMessage;

    public BrowsersViewModel(INativeHostInstaller installer, IPortableEnvironment portable)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(portable);
        _installer = installer;
        IsPortable = portable.IsPortable;
        if (IsPortable)
        {
            StatusMessage = "Browser integration is unavailable in portable mode (it would write to the registry).";
        }

        Refresh();
    }

    /// <summary>Whether the app is portable — registration is disabled and the panel says so (TASK-138).</summary>
    public bool IsPortable { get; }

    /// <summary>Whether browser integration can be (un)registered — never in portable mode.</summary>
    public bool CanManageRegistration => !IsPortable;

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

    [RelayCommand(CanExecute = nameof(CanManageRegistration))]
    private void Register()
    {
        if (IsPortable)
        {
            return; // never write the host manifest to the registry in portable mode (TASK-138)
        }

        StatusMessage = _installer.Install()
            ? "Connected — your browsers can now hand downloads to JustDownload."
            : "Couldn't connect: the native host wasn't found next to the app.";
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistration))]
    private void Unregister()
    {
        if (IsPortable)
        {
            return;
        }

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
