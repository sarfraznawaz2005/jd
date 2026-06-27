using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JustDownload.Core.Categorization;

namespace JustDownload.App.ViewModels;

/// <summary>
/// The category-tree sidebar (TASK-050, PRD 2.4.1): "All Downloads" → per-type category children and a Status
/// group, each with a live count badge. Selecting a node filters the downloads list. It reads its counts from
/// the <see cref="DownloadsListViewModel"/> (the single source of truth) and drives that list's filter, so the
/// two stay in lockstep without the sidebar owning any download state.
/// </summary>
public sealed partial class SidebarViewModel : ViewModelBase, IDisposable
{
    private static readonly (FileCategory Category, string Label)[] CategoryNodes =
    [
        (FileCategory.Video, "Video"),
        (FileCategory.Audio, "Audio"),
        (FileCategory.Document, "Documents"),
        (FileCategory.Compressed, "Compressed"),
        (FileCategory.Program, "Programs"),
        (FileCategory.Image, "Images"),
    ];

    private readonly DownloadsListViewModel _list;

    [ObservableProperty]
    private SidebarNodeViewModel _selectedNode;

    public SidebarViewModel(DownloadsListViewModel list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _list = list;

        All = new SidebarNodeViewModel("All Downloads", "IconNavAll", DownloadFilter.All);
        Nodes.Add(All);
        foreach ((FileCategory category, string label) in CategoryNodes)
        {
            Nodes.Add(SidebarNodeViewModel.ForCategory(category, label));
        }

        Incomplete = new SidebarNodeViewModel(
            "Incomplete", "IconNavIncomplete", new DownloadFilter(DownloadFilterKind.Incomplete));
        Completed = new SidebarNodeViewModel(
            "Completed", "IconNavComplete", new DownloadFilter(DownloadFilterKind.Completed));
        StatusNodes.Add(Incomplete);
        StatusNodes.Add(Completed);

        _selectedNode = All;
        All.IsSelected = true;

        _list.CountsChanged += OnCountsChanged;
        RefreshCounts();
    }

    /// <summary>The Library group: "All Downloads" plus the per-category children.</summary>
    public ObservableCollection<SidebarNodeViewModel> Nodes { get; } = new();

    /// <summary>The Status group: Incomplete / Completed.</summary>
    public ObservableCollection<SidebarNodeViewModel> StatusNodes { get; } = new();

    /// <summary>The "All Downloads" root node.</summary>
    public SidebarNodeViewModel All { get; }

    /// <summary>The Incomplete status node.</summary>
    public SidebarNodeViewModel Incomplete { get; }

    /// <summary>The Completed status node.</summary>
    public SidebarNodeViewModel Completed { get; }

    /// <summary>Selects a node: marks it active and applies its filter to the list.</summary>
    [RelayCommand]
    private void Select(SidebarNodeViewModel? node)
    {
        if (node is null || ReferenceEquals(node, SelectedNode))
        {
            // Re-selecting the active node is a no-op (the filter is already applied).
            if (node is not null)
            {
                _list.ApplyFilter(node.Filter);
            }

            return;
        }

        foreach (SidebarNodeViewModel candidate in AllNodes())
        {
            candidate.IsSelected = ReferenceEquals(candidate, node);
        }

        SelectedNode = node;
        _list.ApplyFilter(node.Filter);
    }

    private void OnCountsChanged(object? sender, EventArgs e) => RefreshCounts();

    private void RefreshCounts()
    {
        DownloadCounts counts = _list.Counts;
        All.Count = counts.All;
        foreach (SidebarNodeViewModel node in Nodes)
        {
            if (node.Filter is { Kind: DownloadFilterKind.Category, Category: { } category })
            {
                node.Count = counts.ForCategory(category);
            }
        }

        Incomplete.Count = counts.Incomplete;
        Completed.Count = counts.Completed;
    }

    private IEnumerable<SidebarNodeViewModel> AllNodes()
    {
        foreach (SidebarNodeViewModel node in Nodes)
        {
            yield return node;
        }

        foreach (SidebarNodeViewModel node in StatusNodes)
        {
            yield return node;
        }
    }

    public void Dispose() => _list.CountsChanged -= OnCountsChanged;
}
