using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.Core.Categorization;
using JustDownload.Core.Settings;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Category settings (TASK-057): the auto-organize toggle and its root folder (both persisted via the
/// <see cref="ISettingsService"/>), plus the per-category destination subfolders the engine sorts into. The
/// folder names come from the shared <see cref="CategoryFolderRules"/>; editing those rules is a separate
/// concern (they are shown read-only here so the layout is transparent).
/// </summary>
public sealed partial class CategoriesSettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private bool _suppress;

    [ObservableProperty]
    private bool _organizeByCategory;

    [ObservableProperty]
    private string _organizedRootDirectory;

    public CategoriesSettingsViewModel(ISettingsService settings, CategoryFolderRules folderRules)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(folderRules);
        _settings = settings;

        _suppress = true;
        AppSettings current = settings.Current;
        _organizeByCategory = current.OrganizeByCategory;
        _organizedRootDirectory = current.OrganizedRootDirectory ?? string.Empty;
        _suppress = false;

        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            Folders.Add(new CategoryFolderRow(category.ToString(), folderRules.GetFolderName(category)));
        }
    }

    /// <summary>The category → subfolder mapping (read-only view of the folder rules).</summary>
    public ObservableCollection<CategoryFolderRow> Folders { get; } = new();

    partial void OnOrganizeByCategoryChanged(bool value)
    {
        if (!_suppress)
        {
            _ = _settings.UpdateAsync(s => s with { OrganizeByCategory = value });
        }
    }

    partial void OnOrganizedRootDirectoryChanged(string value)
    {
        if (!_suppress)
        {
            string? root = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            _ = _settings.UpdateAsync(s => s with { OrganizedRootDirectory = root });
        }
    }

    /// <summary>One row of the category → folder table.</summary>
    public sealed record CategoryFolderRow(string Category, string Folder);
}
