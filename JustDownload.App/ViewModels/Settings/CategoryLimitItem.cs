using CommunityToolkit.Mvvm.ComponentModel;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// One row in the per-category concurrency editor (TASK-141): a category name and its concurrent-download cap
/// (<c>0</c> = unlimited). A change invokes <paramref name="onChanged"/> so the parent can recompose and persist.
/// </summary>
public sealed partial class CategoryLimitItem : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    private int _limit;

    public CategoryLimitItem(string category, int limit, Action onChanged)
    {
        ArgumentException.ThrowIfNullOrEmpty(category);
        ArgumentNullException.ThrowIfNull(onChanged);
        Category = category;
        _limit = limit;
        _onChanged = onChanged;
    }

    public string Category { get; }

    partial void OnLimitChanged(int value) => _onChanged();
}
