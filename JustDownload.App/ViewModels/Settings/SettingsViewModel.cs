using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;
using JustDownload.Core.Abstractions;
using JustDownload.Core.Categorization;
using JustDownload.Core.Logging;
using JustDownload.Core.Security;
using JustDownload.Core.Settings;
using JustDownload.Core.Updates;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// The settings window's view-model (TASK-057, PRD 2.4.5): a navigation rail of the nine sections — General,
/// Video, Connections, Proxy, Authentication, Categories, Browsers, Advanced, Updates — and the selected
/// section's content. General/Video/Connections/Categories/Updates are bound to the persisted
/// <see cref="ISettingsService"/>; the remaining sections describe their (not-yet-a-preference) subsystem
/// honestly until it lands.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly CategoryFolderRules _folderRules;
    private readonly JustDownload.Core.NativeMessaging.INativeHostInstaller _nativeHostInstaller;
    private readonly JustDownload.Core.NativeMessaging.IExtensionContactTracker _extensionContactTracker;
    private readonly ISecretStore _secrets;
    private readonly ISettingsTransfer _transfer;
    private readonly JustDownload.Core.Transport.Proxy.IProxyTester _proxyTester;
    private readonly JustDownload.Core.IPortableEnvironment _portable;
    private readonly ISavedCredentialsService _savedCredentials;
    private readonly JustDownload.Core.Media.IYtDlpLocator _ytDlpLocator;
    private readonly JustDownload.Core.Media.IYtDlpProvisioner _ytDlpProvisioner;
    private readonly IAutostartService _autostart;
    private readonly IUpdateChecker _updateChecker;
    private readonly IAppVersionProvider _appVersion;
    private readonly IErrorLogPathProvider _errorLogPath;
    private readonly IFileRevealer _fileRevealer;

    [ObservableProperty]
    private SettingsSectionViewModel _selectedSection;

    /// <summary>Transient result of the most recent export/import, shown beside the backup buttons.</summary>
    [ObservableProperty]
    private string? _transferStatus;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService theme,
        CategoryFolderRules folderRules,
        JustDownload.Core.NativeMessaging.INativeHostInstaller nativeHostInstaller,
        JustDownload.Core.NativeMessaging.IExtensionContactTracker extensionContactTracker,
        ISecretStore secrets,
        ISettingsTransfer transfer,
        JustDownload.Core.Transport.Proxy.IProxyTester proxyTester,
        JustDownload.Core.IPortableEnvironment portable,
        ISavedCredentialsService savedCredentials,
        JustDownload.Core.Media.IYtDlpLocator ytDlpLocator,
        JustDownload.Core.Media.IYtDlpProvisioner ytDlpProvisioner,
        IAutostartService autostart,
        IUpdateChecker updateChecker,
        IAppVersionProvider appVersion,
        IErrorLogPathProvider errorLogPath,
        IFileRevealer fileRevealer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(folderRules);
        ArgumentNullException.ThrowIfNull(nativeHostInstaller);
        ArgumentNullException.ThrowIfNull(extensionContactTracker);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(transfer);
        ArgumentNullException.ThrowIfNull(proxyTester);
        ArgumentNullException.ThrowIfNull(portable);
        ArgumentNullException.ThrowIfNull(savedCredentials);
        ArgumentNullException.ThrowIfNull(ytDlpLocator);
        ArgumentNullException.ThrowIfNull(ytDlpProvisioner);
        ArgumentNullException.ThrowIfNull(autostart);
        ArgumentNullException.ThrowIfNull(updateChecker);
        ArgumentNullException.ThrowIfNull(appVersion);
        ArgumentNullException.ThrowIfNull(errorLogPath);
        ArgumentNullException.ThrowIfNull(fileRevealer);
        _settings = settings;
        _theme = theme;
        _folderRules = folderRules;
        _nativeHostInstaller = nativeHostInstaller;
        _extensionContactTracker = extensionContactTracker;
        _secrets = secrets;
        _transfer = transfer;
        _proxyTester = proxyTester;
        _portable = portable;
        _savedCredentials = savedCredentials;
        _ytDlpLocator = ytDlpLocator;
        _ytDlpProvisioner = ytDlpProvisioner;
        _autostart = autostart;
        _updateChecker = updateChecker;
        _appVersion = appVersion;
        _errorLogPath = errorLogPath;
        _fileRevealer = fileRevealer;

        PopulateSections();
        _selectedSection = Sections[0];
        _selectedSection.IsSelected = true;
    }

    /// <summary>The nine settings sections in display order.</summary>
    public ObservableCollection<SettingsSectionViewModel> Sections { get; } = new();

    private void PopulateSections()
    {
        Sections.Clear();
        Sections.Add(new SettingsSectionViewModel("General", "IconSetGeneral", new GeneralSettingsViewModel(_settings, _theme, _portable, _autostart)));
        Sections.Add(new SettingsSectionViewModel(
            "Video", "IconSetVideo", new VideoSettingsViewModel(_settings, _ytDlpLocator, _ytDlpProvisioner)));
        Sections.Add(new SettingsSectionViewModel("Connections", "IconSetConnections", new ConnectionsSettingsViewModel(_settings)));
        Sections.Add(new SettingsSectionViewModel("Proxy", "IconSetProxy", new ProxySettingsViewModel(_settings, _secrets, _proxyTester)));
        Sections.Add(new SettingsSectionViewModel(
            "Authentication", "IconSetAuth", new AuthenticationSettingsViewModel(_savedCredentials)));
        Sections.Add(new SettingsSectionViewModel("Categories", "IconSetCategories", new CategoriesSettingsViewModel(_settings, _folderRules)));
        Sections.Add(new SettingsSectionViewModel(
            "Browsers", "IconSetBrowsers", new BrowsersViewModel(_nativeHostInstaller, _extensionContactTracker, _portable)));
        Sections.Add(new SettingsSectionViewModel(
            "Advanced", "IconSetAdvanced",
            new AdvancedSettingsViewModel(_settings, _errorLogPath, _fileRevealer)));
        Sections.Add(new SettingsSectionViewModel(
            "Updates", "IconSetUpdates", new UpdateSettingsViewModel(_settings, _updateChecker, _appVersion)));
    }

    /// <summary>
    /// Exports the current preferences to <paramref name="filePath"/> (TASK-129). The OS file picker is a
    /// top-level concern handled in the window; this drives <see cref="ISettingsTransfer"/> and reports the result.
    /// </summary>
    public async Task ExportToAsync(string filePath)
    {
        try
        {
            await _transfer.ExportAsync(_settings.Current, filePath).ConfigureAwait(true);
            TransferStatus = "Settings exported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TransferStatus = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Imports preferences from <paramref name="filePath"/>, persists them, and rebuilds the sections so the
    /// open window reflects the restored values (TASK-129).
    /// </summary>
    public async Task ImportFromAsync(string filePath)
    {
        try
        {
            AppSettings imported = await _transfer.ImportAsync(filePath).ConfigureAwait(true);
            await _settings.UpdateAsync(_ => imported).ConfigureAwait(true);

            PopulateSections();
            SelectedSection = Sections[0];
            SelectedSection.IsSelected = true;
            TransferStatus = "Settings imported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TransferStatus = $"Import failed: {ex.Message}";
        }
    }

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
