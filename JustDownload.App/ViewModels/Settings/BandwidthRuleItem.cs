using CommunityToolkit.Mvvm.ComponentModel;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// One editable row in the bandwidth-schedule editor (TASK-145): a start/end time (<c>HH:mm</c>) and a cap in
/// MB/s (<c>0</c> = unlimited). Any change invokes <paramref name="onChanged"/> so the parent recomposes and persists.
/// </summary>
public sealed partial class BandwidthRuleItem : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty]
    private string _start;

    [ObservableProperty]
    private string _end;

    [ObservableProperty]
    private double _limitMegabytesPerSecond;

    public BandwidthRuleItem(string start, string end, double limitMegabytesPerSecond, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _start = start;
        _end = end;
        _limitMegabytesPerSecond = limitMegabytesPerSecond;
        _onChanged = onChanged;
    }

    partial void OnStartChanged(string value) => _onChanged();

    partial void OnEndChanged(string value) => _onChanged();

    partial void OnLimitMegabytesPerSecondChanged(double value) => _onChanged();
}
