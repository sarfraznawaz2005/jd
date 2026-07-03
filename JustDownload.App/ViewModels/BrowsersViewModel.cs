using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core;
using JustDownload.Core.NativeMessaging;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The Browsers panel (TASK-093, connection status reworked in TASK-175): lets the user register/unregister
/// the native messaging host, and shows whether each browser family's extension has actually connected.
/// "Connected" (<see cref="BrowserStatusRow"/>) is driven by <see cref="IExtensionContactTracker"/> — real
/// contact the host has observed — not by whether a host manifest file merely exists
/// (<see cref="INativeHostInstaller.GetStatus"/> just reflects that a manifest was written, which happens
/// automatically on every app startup regardless of whether any browser or extension exists; conflating the
/// two made every fresh install show "Connected" for browsers with no extension installed at all). Chrome
/// and Edge share one Chromium extension id (<see cref="NativeHostIdentity.ChromiumExtensionId"/>) and can't
/// be told apart at the native-messaging layer, so they're shown as one row. Drives the engine through
/// <see cref="INativeHostInstaller"/> and <see cref="IExtensionContactTracker"/> only (§6). In portable mode
/// (TASK-138) registration is disabled because it would write the host manifest into the registry — which
/// portable mode must never do.
/// </summary>
public sealed partial class BrowsersViewModel : ViewModelBase
{
    private readonly INativeHostInstaller _installer;
    private readonly IExtensionContactTracker _contactTracker;

    [ObservableProperty]
    private bool _hostAvailable;

    [ObservableProperty]
    private string? _statusMessage;

    public BrowsersViewModel(
        INativeHostInstaller installer, IExtensionContactTracker contactTracker, IPortableEnvironment portable)
    {
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(contactTracker);
        ArgumentNullException.ThrowIfNull(portable);
        _installer = installer;
        _contactTracker = contactTracker;
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

    /// <summary>Per-browser-family connection status, driven by real observed contact (TASK-175).</summary>
    public ObservableCollection<BrowserStatusRow> Browsers { get; } = new();

    [RelayCommand]
    private void Refresh()
    {
        HostAvailable = _installer.IsHostPresent;
        Browsers.Clear();
        Browsers.Add(new BrowserStatusRow(
            "Chromium browsers (Chrome, Edge)",
            _contactTracker.GetLastContact(ExtensionContactOrigin.Chromium) is not null));
        Browsers.Add(new BrowserStatusRow(
            "Firefox",
            _contactTracker.GetLastContact(ExtensionContactOrigin.Firefox) is not null));
    }

    [RelayCommand(CanExecute = nameof(CanManageRegistration))]
    private void Register()
    {
        if (IsPortable)
        {
            return; // never write the host manifest to the registry in portable mode (TASK-138)
        }

        // Registering only makes the host *reachable* — it doesn't mean anything has connected yet, so this
        // deliberately doesn't say "Connected" (TASK-175).
        StatusMessage = _installer.Install()
            ? "Host registered — install the browser extension to finish connecting."
            : "Couldn't register: the native host wasn't found next to the app.";
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

/// <summary>One browser family's connection state in the Browsers panel (TASK-093, TASK-175).</summary>
public sealed record BrowserStatusRow(string Name, bool IsConnected)
{
    /// <summary>Human-readable status for the row.</summary>
    public string StatusText => IsConnected ? "Connected" : "Not connected";
}
