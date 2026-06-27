using CommunityToolkit.Mvvm.Input;
using JustDownload.App.Services;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The main window's view-model (TASK-047). For the shell it owns the window title and the theme toggle; the
/// download list, categories and detail bind in via their own view-models in later tasks (048/049). Depends
/// only on interfaces (§6) so it is testable in isolation.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IThemeService _theme;

    public MainWindowViewModel(IThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
    }

    /// <summary>Cycles the application theme (Light → Dark → System).</summary>
    [RelayCommand]
    private void ToggleTheme() => _theme.Toggle();
}
