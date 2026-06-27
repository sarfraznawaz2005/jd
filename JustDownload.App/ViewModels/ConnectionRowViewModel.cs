using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Formatting;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One row in the per-download detail view's Connections tab (TASK-054, US-15c): the live state of a single
/// connection — the byte range it owns, how much it has fetched, its speed, and whether it is still active.
/// Updated in place from the engine's <see cref="ConnectionStat"/> snapshots so the list does not churn.
/// </summary>
public sealed partial class ConnectionRowViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _rangeDisplay = "—";

    [ObservableProperty]
    private string _downloadedDisplay = "—";

    [ObservableProperty]
    private string _speedDisplay = "—";

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _stateDisplay = "active";

    [ObservableProperty]
    private bool _isActive = true;

    public ConnectionRowViewModel(ConnectionStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        ConnectionId = stat.ConnectionId;
        Apply(stat);
    }

    /// <summary>The stable connection id (0-based); the row's identity for in-place updates.</summary>
    public int ConnectionId { get; }

    /// <summary>The 1-based number shown to the user.</summary>
    public int DisplayNumber => ConnectionId + 1;

    /// <summary>Applies the latest stat snapshot to the live columns.</summary>
    public void Apply(ConnectionStat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        RangeDisplay = $"{ByteFormatter.FormatSize(stat.Start)} – {ByteFormatter.FormatSize(stat.End)}";
        DownloadedDisplay = ByteFormatter.FormatSize(stat.DownloadedBytes);
        SpeedDisplay = stat.IsActive ? ByteFormatter.FormatSpeed(stat.BytesPerSecond) : "—";
        ProgressPercent = stat.Fraction * 100;
        IsActive = stat.IsActive;
        StateDisplay = stat.IsActive ? "active" : "done";
    }
}
