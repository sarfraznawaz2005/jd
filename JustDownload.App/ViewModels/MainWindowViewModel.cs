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
    private readonly IDensityService _density;

    /// <summary>Width (DIPs) below which the sidebar auto-hides so the list+detail still fit (TASK-048).</summary>
    public const double NarrowBreakpoint = 940;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarVisible))]
    private bool _sidebarCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarVisible))]
    private bool _isNarrow;

    public MainWindowViewModel(
        IThemeService theme,
        IDensityService density,
        StatusSummaryViewModel status,
        DownloadsListViewModel downloads,
        DownloadDetailViewModel detail,
        SidebarViewModel sidebar)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(density);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(sidebar);
        _theme = theme;
        _density = density;
        Status = status;
        Downloads = downloads;
        Detail = detail;
        Sidebar = sidebar;

        // Keep the detail pane pointed at the list's current selection (TASK-054).
        Downloads.PropertyChanged += OnDownloadsPropertyChanged;

        // Re-apply the compact style class whenever density changes (here or via the settings screen).
        _density.Changed += (_, _) => OnPropertyChanged(nameof(IsCompact));

        Palette = new CommandPaletteViewModel(BuildPaletteCommands());
    }

    /// <summary>The Ctrl/Cmd+K command palette over the shell's core commands (TASK-056).</summary>
    public CommandPaletteViewModel Palette { get; }

    /// <summary>Whether the list/detail use the compact (power-user) density (TASK-063). Drives the shell style.</summary>
    public bool IsCompact => _density.IsCompact;

    /// <summary>The live status-bar summary (active count, total speed, connections).</summary>
    public StatusSummaryViewModel Status { get; }

    /// <summary>The downloads list shown in the master pane (TASK-051).</summary>
    public DownloadsListViewModel Downloads { get; }

    /// <summary>The per-download detail shown in the detail pane (TASK-054).</summary>
    public DownloadDetailViewModel Detail { get; }

    /// <summary>The category-tree sidebar that filters the list (TASK-050).</summary>
    public SidebarViewModel Sidebar { get; }

    /// <summary>The sidebar is shown when the user hasn't collapsed it and the window is wide enough (TASK-048).</summary>
    public bool SidebarVisible => !SidebarCollapsed && !IsNarrow;

    /// <summary>
    /// Updates the responsive layout from the current window width (TASK-048): below
    /// <see cref="NarrowBreakpoint"/> the sidebar auto-hides so the list and detail panes still fit without
    /// the list being squeezed below its usable minimum (keeps 800x600 workable).
    /// </summary>
    public void UpdateForWidth(double width) => IsNarrow = width < NarrowBreakpoint;

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

    /// <summary>Switches between Comfortable and Compact list/detail density (TASK-063); persists the choice.</summary>
    [RelayCommand]
    private void ToggleDensity() => _density.Toggle();

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

    /// <summary>Opens the command palette (Ctrl/Cmd+K).</summary>
    [RelayCommand]
    private void OpenPalette() => Palette.Open();

    /// <summary>
    /// Builds the palette's command set (TASK-056): the core actions plus a "Go to …" entry for every sidebar
    /// category and status node so the user can jump the list filter from the keyboard.
    /// </summary>
    private List<PaletteCommand> BuildPaletteCommands()
    {
        var commands = new List<PaletteCommand>
        {
            new("New URL…", "Actions", () => NewDownloadRequested?.Invoke(this, EventArgs.Empty),
                "new", "add", "download", "url", "link"),
            new("Toggle theme", "Actions", () => _theme.Toggle(),
                "theme", "dark", "light", "appearance"),
            new("Toggle density", "Actions", () => _density.Toggle(),
                "density", "compact", "comfortable", "layout"),
            new("Change limits…", "Actions", () => SettingsRequested?.Invoke(this, EventArgs.Empty),
                "settings", "preferences", "limit", "speed", "connections", "concurrent"),
        };

        foreach (SidebarNodeViewModel node in Sidebar.Nodes)
        {
            SidebarNodeViewModel target = node;
            commands.Add(new PaletteCommand(
                $"Go to {target.Label}", "Jump to", () => Sidebar.SelectCommand.Execute(target), "category", "filter"));
        }

        foreach (SidebarNodeViewModel node in Sidebar.StatusNodes)
        {
            SidebarNodeViewModel target = node;
            commands.Add(new PaletteCommand(
                $"Go to {target.Label}", "Jump to", () => Sidebar.SelectCommand.Execute(target), "status", "filter"));
        }

        return commands;
    }
}
