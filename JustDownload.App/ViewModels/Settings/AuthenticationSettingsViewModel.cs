using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.Security;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Authentication settings (TASK-126): lists the credentials the app has saved to the OS keychain — the global
/// proxy password and any per-download cookie/proxy secrets — and lets the user revoke them. Only metadata is
/// shown (what the credential is for), never the secret value (§5). Removal deletes the keychain entry and
/// clears its reference through <see cref="ISavedCredentialsService"/>.
/// </summary>
public sealed partial class AuthenticationSettingsViewModel : ViewModelBase
{
    private readonly ISavedCredentialsService _credentials;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSavedCredentials))]
    private bool _loaded;

    public AuthenticationSettingsViewModel(ISavedCredentialsService credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _credentials = credentials;
        _ = LoadAsync();
    }

    /// <summary>The saved credentials, by non-secret description.</summary>
    public ObservableCollection<SavedCredentialRow> Credentials { get; } = new();

    /// <summary>Whether to show the "nothing saved" empty state (after a load that found none).</summary>
    public bool HasNoSavedCredentials => Loaded && Credentials.Count == 0;

    /// <summary>(Re)loads the saved-credential list from the engine.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<SavedCredential> saved = await _credentials.ListAsync().ConfigureAwait(true);
        Credentials.Clear();
        foreach (SavedCredential credential in saved)
        {
            Credentials.Add(new SavedCredentialRow(credential));
        }

        Loaded = true;
        OnPropertyChanged(nameof(HasNoSavedCredentials));
    }

    [RelayCommand]
    private async Task RemoveAsync(SavedCredentialRow? row)
    {
        if (row is null)
        {
            return;
        }

        await _credentials.RemoveAsync(row.Model).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }
}

/// <summary>One saved credential in the Authentication panel — its description only, never the secret (TASK-126).</summary>
public sealed record SavedCredentialRow(SavedCredential Model)
{
    public string Description => Model.Description;
}
