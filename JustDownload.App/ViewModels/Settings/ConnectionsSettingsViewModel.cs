using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.Core.Categorization;
using JustDownload.Core.Lifecycle;
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
    [NotifyPropertyChangedFor(nameof(ConnectionsPerDownloadError))]
    private int _connectionsPerDownload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxConcurrentDownloadsError))]
    private int _maxConcurrentDownloads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedLimitError))]
    private bool _speedLimited;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedLimitDisplay))]
    [NotifyPropertyChangedFor(nameof(SpeedLimitError))]
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

        IReadOnlyDictionary<string, int> caps = CategoryConcurrency.Parse(current.CategoryConcurrencyLimits);
        CategoryLimits = new ObservableCollection<CategoryLimitItem>(
            CappableCategories.Select(c =>
                new CategoryLimitItem(c.ToString(), caps.GetValueOrDefault(c.ToString()), PersistCategoryLimits)));
        _suppress = false;
    }

    /// <summary>The concrete categories that can be given a concurrency cap (everything except the catch-all).</summary>
    private static IReadOnlyList<FileCategory> CappableCategories { get; } =
        [FileCategory.Video, FileCategory.Audio, FileCategory.Document, FileCategory.Compressed,
         FileCategory.Program, FileCategory.Image];

    /// <summary>Per-category concurrent-download caps (TASK-141); <c>0</c> means unlimited for that category.</summary>
    public ObservableCollection<CategoryLimitItem> CategoryLimits { get; }

    /// <summary>The largest per-category cap the UI allows (the global concurrent ceiling).</summary>
    public int MaxCategoryConcurrent { get; } = MaxConcurrent;

    /// <summary>The valid range for connections per download (dynamic segmentation, 1–32).</summary>
    public const int MinConnections = 1;
    public const int MaxConnections = 32;
    public const int MinConcurrent = 1;
    public const int MaxConcurrent = 16;

    /// <summary>Human-readable summary of the speed cap for the section.</summary>
    public string SpeedLimitDisplay => SpeedLimited
        ? $"{SpeedLimitMegabytesPerSecond:0.0} MB/s"
        : "Unlimited";

    /// <summary>Inline validation for connections-per-download (TASK-128), or null when in range.</summary>
    public string? ConnectionsPerDownloadError =>
        ConnectionsPerDownload < MinConnections || ConnectionsPerDownload > MaxConnections
            ? $"Enter a value between {MinConnections} and {MaxConnections}."
            : null;

    /// <summary>Inline validation for the concurrent-download cap (TASK-128), or null when in range.</summary>
    public string? MaxConcurrentDownloadsError =>
        MaxConcurrentDownloads < MinConcurrent || MaxConcurrentDownloads > MaxConcurrent
            ? $"Enter a value between {MinConcurrent} and {MaxConcurrent}."
            : null;

    /// <summary>Inline validation for the speed cap when limiting is on (TASK-128), or null when valid.</summary>
    public string? SpeedLimitError =>
        SpeedLimited && SpeedLimitMegabytesPerSecond <= 0
            ? "Enter a speed greater than 0 MB/s."
            : null;

    partial void OnConnectionsPerDownloadChanged(int value)
    {
        // Surface out-of-range input (TASK-128) and don't persist it; the prior valid value is kept until
        // the user enters one in range.
        if (!_suppress && ConnectionsPerDownloadError is null)
        {
            _ = _settings.UpdateAsync(s => s with { ConnectionsPerDownload = value });
        }
    }

    partial void OnMaxConcurrentDownloadsChanged(int value)
    {
        if (!_suppress && MaxConcurrentDownloadsError is null)
        {
            _ = _settings.UpdateAsync(s => s with { MaxConcurrentDownloads = value });
        }
    }

    partial void OnSpeedLimitedChanged(bool value) => PersistSpeedLimit();

    partial void OnSpeedLimitMegabytesPerSecondChanged(double value) => PersistSpeedLimit();

    private void PersistSpeedLimit()
    {
        if (_suppress || SpeedLimitError is not null)
        {
            return;
        }

        long bytes = SpeedLimited
            ? (long)Math.Round(SpeedLimitMegabytesPerSecond * BytesPerMegabyte)
            : 0;
        _ = _settings.UpdateAsync(s => s with { GlobalSpeedLimitBytesPerSecond = bytes });
    }

    private void PersistCategoryLimits()
    {
        if (_suppress)
        {
            return;
        }

        Dictionary<string, int> caps = CategoryLimits
            .Where(item => item.Limit > 0)
            .ToDictionary(item => item.Category, item => item.Limit, StringComparer.Ordinal);
        string canonical = CategoryConcurrency.Format(caps);
        _ = _settings.UpdateAsync(s => s with
        {
            CategoryConcurrencyLimits = canonical.Length == 0 ? null : canonical,
        });
    }
}
