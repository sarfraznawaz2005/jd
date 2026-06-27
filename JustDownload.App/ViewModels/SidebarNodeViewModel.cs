using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Converters;
using JustDownload.Core.Categorization;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One node in the category-tree sidebar (TASK-050): a label, an icon, a live count badge, and the
/// <see cref="DownloadFilter"/> it applies when selected. Covers the "All Downloads" root, the per-category
/// children, and the Status group nodes.
/// </summary>
public sealed partial class SidebarNodeViewModel : ViewModelBase
{
    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isSelected;

    public SidebarNodeViewModel(string label, string iconKey, DownloadFilter filter, bool isChild = false)
    {
        Label = label;
        IconKey = iconKey;
        Filter = filter;
        IsChild = isChild;
    }

    /// <summary>The display label.</summary>
    public string Label { get; }

    /// <summary>The resource key of the node's icon geometry (resolved via <see cref="ResourceKeyConverter"/>).</summary>
    public string IconKey { get; }

    /// <summary>The filter applied to the downloads list when this node is selected.</summary>
    public DownloadFilter Filter { get; }

    /// <summary>Whether the node is a child (indented under a parent).</summary>
    public bool IsChild { get; }

    /// <summary>Builds the category-child node for <paramref name="category"/>.</summary>
    public static SidebarNodeViewModel ForCategory(FileCategory category, string label) =>
        new(label, CategoryVisuals.GeometryKey(category), new DownloadFilter(DownloadFilterKind.Category, category), isChild: true);
}
