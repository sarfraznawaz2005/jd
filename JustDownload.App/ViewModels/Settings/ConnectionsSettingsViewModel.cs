using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.Core.Settings;

namespace JustDownload.App.ViewModels.Settings;

/// <summary>
/// Connection settings (TASK-057): the default connections per download, the concurrent-download cap, and the
/// global speed limit. All persist through the <see cref="ISettingsService"/>; because the engine reads these
/// from the same snapshot, edits take effect for subsequent downloads immediately.
/// </summary>
public sealed partial class ConnectionsSettingsViewModel : ViewModelBase
{
    private const long BytesPerMegabyte = 1024 * 1024;

    private readonly ISettingsService _settings;
    private bool _suppress;

    [ObservableProperty]
    private int _connectionsPerDownload;

    [ObservableProperty]
    private int _maxConcurrentDownloads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitDisplay))]
    private bool _speedLimited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitDisplay))]
    private double _speedLimitMegabytesPerSecond;

    public ConnectionsSettingsViewModel(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        _suppress = true;
        AppSettings current = settings.Current;
        _connectionsPerDownload = current.ConnectionsPerDownload;
        _maxConcurrentDownloads = current.MaxConcurrentDownloads;
        _speedLimited = current.GlobalSpeedLimitBytesPerSecond > 0;
        _speedLimitMegabytesPerSecond = current.GlobalSpeedLimitBytesPerSecond > 0
            ? Math.Round((double)current.GlobalSpeedLimitBytesPerSecond / BytesPerMegabyte, 1)
            : 1.0;
        _suppress = false;
    }

    /// <summary>The valid range for connections per download (dynamic segmentation, 1–32).</summary>
    public const int MinConnections = 1;
    public const int MaxConnections = 32;
    public const int MinConcurrent = 1;
    public const int MaxConcurrent = 16;

    /// <summary>Human-readable summary of the speed cap for the section.</summary>
    public string SpeedLimitDisplay => SpeedLimited
        ? $"{SpeedLimitMegabytesPerSecond:0.0} MB/s"
        : "Unlimited";

    partial void OnConnectionsPerDownloadChanged(int value)
    {
        if (!_suppress)
        {
            int clamped = Math.Clamp(value, MinConnections, MaxConnections);
            _ = _settings.UpdateAsync(s => s with { ConnectionsPerDownload = clamped });
        }
    }

    partial void OnMaxConcurrentDownloadsChanged(int value)
    {
        if (!_suppress)
        {
            int clamped = Math.Clamp(value, MinConcurrent, MaxConcurrent);
            _ = _settings.UpdateAsync(s => s with { MaxConcurrentDownloads = clamped });
        }
    }

    partial void OnSpeedLimitedChanged(bool value) => PersistSpeedLimit();

    partial void OnSpeedLimitMegabytesPerSecondChanged(double value) => PersistSpeedLimit();

    private void PersistSpeedLimit()
    {
        if (_suppress)
        {
            return;
        }

        long bytes = SpeedLimited
            ? (long)Math.Round(Math.Max(0, SpeedLimitMegabytesPerSecond) * BytesPerMegabyte)
            : 0;
        _ = _settings.UpdateAsync(s => s with { GlobalSpeedLimitBytesPerSecond = bytes });
    }
}
