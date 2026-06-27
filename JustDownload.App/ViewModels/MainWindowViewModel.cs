using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The main window's view-model (TASK-047/049). Owns the three-pane shell state — sidebar collapse, the theme
/// toggle, and the live status summary. The download list and per-download detail bind in via their own
/// view-models in later tasks (051/054). Depends only on interfaces (§6) so it is testable in isolation.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IThemeService _theme;

    [ObservableProperty]
    private bool _sidebarCollapsed;

    public MainWindowViewModel(
        IThemeService theme,
        StatusSummaryViewModel status,
        DownloadsListViewModel downloads,
        DownloadDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(detail);
        _theme = theme;
        Status = status;
        Downloads = downloads;
        Detail = detail;

        // Keep the detail pane pointed at the list's current selection (TASK-054).
        Downloads.PropertyChanged += OnDownloadsPropertyChanged;
    }

    /// <summary>The live status-bar summary (active count, total speed, connections).</summary>
    public StatusSummaryViewModel Status { get; }

    /// <summary>The downloads list shown in the master pane (TASK-051).</summary>
    public DownloadsListViewModel Downloads { get; }

    /// <summary>The per-download detail shown in the detail pane (TASK-054).</summary>
    public DownloadDetailViewModel Detail { get; }

    private void OnDownloadsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadsListViewModel.SelectedDownload))
        {
            Detail.Select(Downloads.SelectedDownload);
        }
    }

    /// <summary>The application version, shown in the About flyout.</summary>
    public string AppVersion { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(MainWindowViewModel).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip any source-control metadata suffix (e.g. "1.0.0+abc123").
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    /// <summary>Raised when the user invokes "New URL" — the shell opens the new-download dialog (TASK-053).</summary>
    public event EventHandler? NewDownloadRequested;

    /// <summary>Raised when the user opens Settings — the shell shows the settings screens (TASK-057).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when the user opens the Browsers panel (extension/browser integration).</summary>
    public event EventHandler? BrowsersRequested;

    /// <summary>Cycles the application theme (Light → Dark → System).</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

    /// <summary>Collapses or restores the sidebar pane.</summary>
    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    /// <summary>Starts a new download: signals the shell to open the New URL dialog (TASK-053).</summary>
    [RelayCommand]
    private void NewDownload() => NewDownloadRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the application settings (TASK-057).</summary>
    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the connected-browsers panel.</summary>
    [RelayCommand]
    private void ShowBrowsers() => BrowsersRequested?.Invoke(this, EventArgs.Empty);
}
