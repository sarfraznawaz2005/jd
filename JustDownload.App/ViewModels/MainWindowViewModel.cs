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

    public MainWindowViewModel(IThemeService theme, StatusSummaryViewModel status, DownloadsListViewModel downloads)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(downloads);
        _theme = theme;
        Status = status;
        Downloads = downloads;
    }

    /// <summary>The live status-bar summary (active count, total speed, connections).</summary>
    public StatusSummaryViewModel Status { get; }

    /// <summary>The downloads list shown in the master pane (TASK-051).</summary>
    public DownloadsListViewModel Downloads { get; }

    /// <summary>Cycles the application theme (Light → Dark → System).</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();

    /// <summary>Collapses or restores the sidebar pane.</summary>
    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;
}
