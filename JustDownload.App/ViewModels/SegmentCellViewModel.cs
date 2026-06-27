using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One block in the segment visualization strip (TASK-055): a single connection's live fill. Updated in place
/// so the strip animates smoothly rather than rebuilding each repaint.
/// </summary>
public sealed partial class SegmentCellViewModel : ViewModelBase
{
    [ObservableProperty]
    private double _fillPercent;

    [ObservableProperty]
    private bool _isActive = true;

    public SegmentCellViewModel(ConnectionStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        ConnectionId = stat.ConnectionId;
        Apply(stat);
    }

    /// <summary>The connection this block represents (its identity for in-place updates).</summary>
    public int ConnectionId { get; }

    /// <summary>Applies the latest stat to the block's fill and active state.</summary>
    public void Apply(ConnectionStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        FillPercent = stat.Fraction * 100;
        IsActive = stat.IsActive;
    }
}
