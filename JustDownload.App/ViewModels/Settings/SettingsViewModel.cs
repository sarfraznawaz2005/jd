using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;
using JustDownload.Core.Categorization;
using JustDownload.Core.Settings;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// The settings window's view-model (TASK-057, PRD 2.4.5): a navigation rail of the seven sections — General,
/// Connections, Proxy, Authentication, Categories, Browsers, Advanced — and the selected section's content.
/// General/Connections/Categories are bound to the persisted <see cref="ISettingsService"/>; the remaining
/// sections describe their (not-yet-a-preference) subsystem honestly until it lands.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private SettingsSectionViewModel _selectedSection;

    public SettingsViewModel(ISettingsService settings, IThemeService theme, CategoryFolderRules folderRules)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(folderRules);

        Sections.Add(new SettingsSectionViewModel("General", "IconSetGeneral", new GeneralSettingsViewModel(settings, theme)));
        Sections.Add(new SettingsSectionViewModel("Connections", "IconSetConnections", new ConnectionsSettingsViewModel(settings)));
        Sections.Add(new SettingsSectionViewModel("Proxy", "IconSetProxy", new InfoSettingsViewModel(
            "Proxy",
            "JustDownload uses your system proxy by default.",
            "Per-download proxy configuration (HTTP/SOCKS with Basic, Digest, or NTLM auth) is applied at "
            + "download time and is not yet a saved global preference — it arrives with the proxy subsystem.")));
        Sections.Add(new SettingsSectionViewModel("Authentication", "IconSetAuth", new InfoSettingsViewModel(
            "Authentication",
            "Site and proxy credentials are entered when a download needs them and are stored securely in the "
            + "OS keychain (DPAPI / macOS Keychain / libsecret) — never in plain text.",
            "There are no global credentials to configure here.")));
        Sections.Add(new SettingsSectionViewModel("Categories", "IconSetCategories", new CategoriesSettingsViewModel(settings, folderRules)));
        Sections.Add(new SettingsSectionViewModel("Browsers", "IconSetBrowsers", new InfoSettingsViewModel(
            "Browsers",
            "Install the JustDownload browser extension to capture downloads and media from your browser.",
            "The extension talks to the app over a Native Messaging host — no local network port is opened. "
            + "Connected browsers will be managed here.")));
        Sections.Add(new SettingsSectionViewModel("Advanced", "IconSetAdvanced", new InfoSettingsViewModel(
            "Advanced",
            "Logs redact credentials, tokens, and signed-URL query strings.",
            "There is no telemetry and no phone-home; the only network traffic is your downloads and an opt-in "
            + "update check.")));

        _selectedSection = Sections[0];
        _selectedSection.IsSelected = true;
    }

    /// <summary>The seven settings sections in display order.</summary>
    public ObservableCollection<SettingsSectionViewModel> Sections { get; } = new();

    /// <summary>Selects a section (marks it active and shows its content).</summary>
    [RelayCommand]
    private void Select(SettingsSectionViewModel? section)
    {
        if (section is null)
        {
            return;
        }

        foreach (SettingsSectionViewModel candidate in Sections)
        {
            candidate.IsSelected = ReferenceEquals(candidate, section);
        }

        SelectedSection = section;
    }
}
