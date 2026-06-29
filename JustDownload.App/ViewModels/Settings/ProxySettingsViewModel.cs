using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Transport.Auth;
using JustDownload.Core.Transport.Proxy;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Proxy settings (TASK-125): a global HTTP/SOCKS4/SOCKS5 proxy with optional Basic/Digest/NTLM auth, backed
/// by the engine's proxy subsystem. Host/port/kind/username/domain persist through the
/// <see cref="ISettingsService"/>; the password is written to the OS keychain via <see cref="ISecretStore"/>
/// (§5) and only its opaque reference is persisted. The password field is never pre-filled — leave it blank
/// to keep the stored password. Saving applies live through <see cref="GlobalProxyController"/>.
/// </summary>
public sealed partial class ProxySettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly IProxyTester _proxyTester;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyError))]
    [NotifyPropertyChangedFor(nameof(RequiresHost))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private ProxyKind _proxyKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private string _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProxyError))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private int _port;

    [ObservableProperty]
    private string _username;

    [ObservableProperty]
    private string _domain;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _hasStoredPassword;

    [ObservableProperty]
    private string? _status;

    public ProxySettingsViewModel(ISettingsService settings, ISecretStore secrets, IProxyTester proxyTester)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(proxyTester);
        _settings = settings;
        _secrets = secrets;
        _proxyTester = proxyTester;

        AppSettings current = settings.Current;
        _proxyKind = current.ProxyKind;
        _host = current.ProxyHost ?? string.Empty;
        _port = current.ProxyPort;
        _username = current.ProxyUsername ?? string.Empty;
        _domain = current.ProxyDomain ?? string.Empty;
        _hasStoredPassword = !string.IsNullOrEmpty(current.ProxyPasswordSecretRef);
    }

    public IReadOnlyList<ProxyKind> ProxyKinds { get; } =
        [ProxyKind.None, ProxyKind.Http, ProxyKind.Socks4, ProxyKind.Socks5];

    /// <summary>Whether a host/port are required (any proxy kind other than None).</summary>
    public bool RequiresHost => ProxyKind != ProxyKind.None;

    /// <summary>Inline validation, or null when the form is valid to save.</summary>
    public string? ProxyError
    {
        get
        {
            if (ProxyKind == ProxyKind.None)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(Host))
            {
                return "Enter the proxy host.";
            }

            return Port is < 1 or > 65535 ? "Enter a port between 1 and 65535." : null;
        }
    }

    private bool CanSave => ProxyError is null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        string? host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        ProxyKind kind = host is null ? ProxyKind.None : ProxyKind;
        string? username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        string? domain = string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim();

        string? secretRef = _settings.Current.ProxyPasswordSecretRef;
        if (kind == ProxyKind.None || username is null)
        {
            // No proxy or no auth — discard any stored password.
            await DeleteSecretAsync(secretRef).ConfigureAwait(true);
            secretRef = null;
        }
        else if (!string.IsNullOrEmpty(Password))
        {
            // A new password was entered — replace the stored one.
            await DeleteSecretAsync(secretRef).ConfigureAwait(true);
            secretRef = await _secrets.StoreAsync(Password).ConfigureAwait(true);
        }

        // Otherwise the password field was left blank: keep the existing secret reference.
        await _settings.UpdateAsync(s => s with
        {
            ProxyKind = kind,
            ProxyHost = host,
            ProxyPort = Port,
            ProxyUsername = username,
            ProxyDomain = domain,
            ProxyPasswordSecretRef = secretRef,
        }).ConfigureAwait(true);

        Password = string.Empty; // never keep the plaintext in the field
        HasStoredPassword = !string.IsNullOrEmpty(secretRef);
        Status = "Proxy settings saved.";
    }

    private bool CanTest => ProxyError is null && ProxyKind != ProxyKind.None;

    /// <summary>
    /// Probes the currently-entered proxy config (including a just-typed but unsaved password, else the
    /// stored one) and reports the result inline (TASK-152).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync(CancellationToken cancellationToken)
    {
        Status = "Testing proxy…";
        ProxyConfiguration config = await BuildCurrentConfigAsync(cancellationToken).ConfigureAwait(true);
        ProxyTestResult result = await _proxyTester.TestAsync(config, cancellationToken).ConfigureAwait(true);
        Status = result.Message;
    }

    private async Task<ProxyConfiguration> BuildCurrentConfigAsync(CancellationToken cancellationToken)
    {
        string? host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        if (ProxyKind == ProxyKind.None || host is null)
        {
            return ProxyConfiguration.None;
        }

        string? username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        NetworkCredentials? credentials = null;
        if (username is not null)
        {
            string? password = !string.IsNullOrEmpty(Password)
                ? Password
                : await ResolveStoredPasswordAsync(cancellationToken).ConfigureAwait(true);
            string? domain = string.IsNullOrWhiteSpace(Domain) ? null : Domain.Trim();
            credentials = new NetworkCredentials(username, password ?? string.Empty, domain);
        }

        return new ProxyConfiguration(ProxyKind, host, Port, credentials);
    }

    private async Task<string?> ResolveStoredPasswordAsync(CancellationToken cancellationToken)
    {
        string? secretRef = _settings.Current.ProxyPasswordSecretRef;
        return string.IsNullOrEmpty(secretRef)
            ? null
            : await _secrets.RetrieveAsync(secretRef, cancellationToken).ConfigureAwait(true);
    }

    private async Task DeleteSecretAsync(string? secretRef)
    {
        if (!string.IsNullOrEmpty(secretRef))
        {
            await _secrets.DeleteAsync(secretRef).ConfigureAwait(true);
        }
    }

    partial void OnHostChanged(string value) => Status = null;

    partial void OnProxyKindChanged(ProxyKind value) => Status = null;

    partial void OnPasswordChanged(string value) => Status = null;
}
