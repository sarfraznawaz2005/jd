using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One stacked strip in the segment visualization (TASK-055): a labelled block-strip of per-connection cells
/// plus the live "Segments: N" count. Cells are reconciled in place by connection id so the strip reflects
/// live adds/removes (work-steals) without rebuilding.
/// </summary>
public sealed partial class StreamStripViewModel : ViewModelBase
{
    private readonly Dictionary<int, SegmentCellViewModel> _cellsById = [];

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private int _segmentCount;

    public StreamStripViewModel(string label)
    {
        _label = label;
    }

    /// <summary>The connection blocks, one per active/known connection.</summary>
    public ObservableCollection<SegmentCellViewModel> Cells { get; } = [];

    /// <summary>Reconciles the strip's cells and segment count against the latest connection stats.</summary>
    public void Update(string label, IReadOnlyList<ConnectionStat> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        Label = label;

        var seen = new HashSet<int>(connections.Count);
        foreach (ConnectionStat stat in connections)
        {
            seen.Add(stat.ConnectionId);
            if (_cellsById.TryGetValue(stat.ConnectionId, out SegmentCellViewModel? cell))
            {
                cell.Apply(stat);
            }
            else
            {
                var created = new SegmentCellViewModel(stat);
                _cellsById[stat.ConnectionId] = created;
                Cells.Add(created);
            }
        }

        for (int i = Cells.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Cells[i].ConnectionId))
            {
                _cellsById.Remove(Cells[i].ConnectionId);
                Cells.RemoveAt(i);
            }
        }

        SegmentCount = Cells.Count;
    }
}
