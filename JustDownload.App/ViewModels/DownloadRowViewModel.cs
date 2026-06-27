using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using JustDownload.App.Formatting;
using JustDownload.Core.Categorization;
using JustDownload.Core.Data.Models;
using JustDownload.Core.Lifecycle;

namespace JustDownload.App.ViewModels;

/// <summary>
/// One row in the downloads list (TASK-051): the file's identity and category icon plus the live status
/// cell (label + inline progress) and the speed/ETA columns that update from the engine's progress events.
/// Static columns (name, size, added) are derived once from the persisted <see cref="Download"/>; the live
/// columns are refreshed through <see cref="ApplyProgress"/> / <see cref="ApplyStatus"/>. The label/percent
/// math lives in the pure <see cref="BuildLabel"/> so it is unit-testable in isolation (§3).
/// </summary>
public sealed partial class DownloadRowViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsExpired))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanRenew))]
    [NotifyPropertyChangedFor(nameof(CanOpenFile))]
    private DownloadStatus _status;

    [ObservableProperty]
    private string _statusLabel = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _showProgressBar;

    [ObservableProperty]
    private string _speedDisplay = "—";

    [ObservableProperty]
    private string _etaDisplay = "—";

    public DownloadRowViewModel(Download download, DateTimeOffset now, FileCategory category)
    {
        ArgumentNullException.ThrowIfNull(download);
        Id = download.Id;
        Url = download.Url;
        FilePath = ResolveFilePath(download);
        Category = category;
        FileName = string.IsNullOrWhiteSpace(download.Filename) ? DeriveNameFromUrl(download.Url) : download.Filename!;
        SubLine = BuildSubLine(download);
        TotalBytes = download.TotalBytes;
        SizeDisplay = download.TotalBytes is > 0 ? ByteFormatter.FormatSize(download.TotalBytes.Value) : "—";
        AddedDisplay = TimeFormatter.FormatRelative(download.CreatedAt, now);
        AddedSortKey = download.CreatedAt;

        Status = DownloadStatusCodes.Parse(download.Status);
        StatusLabel = BuildLabel(Status, fraction: null);
    }

    /// <summary>The download's primary key — the identity the action commands operate on.</summary>
    public long Id { get; }

    /// <summary>The source URL (used by the "copy link" action and renew flows).</summary>
    public string Url { get; }

    /// <summary>The absolute destination path when both directory and name are known; otherwise <c>null</c>.</summary>
    public string? FilePath { get; }

    /// <summary>The file-type category that selects the row's icon and tint.</summary>
    public FileCategory Category { get; }

    /// <summary>The display file name (the primary line of the name cell).</summary>
    public string FileName { get; }

    /// <summary>The secondary line under the name (host and any extra context).</summary>
    public string SubLine { get; }

    /// <summary>Total size in bytes when known, used as the sort key for the size column.</summary>
    public long? TotalBytes { get; }

    /// <summary>The formatted size column (e.g. <c>52.5 MB</c>) or <c>—</c> when unknown.</summary>
    public string SizeDisplay { get; }

    /// <summary>The formatted "Added" column (e.g. <c>2h ago</c>).</summary>
    public string AddedDisplay { get; }

    /// <summary>The creation timestamp, used as the sort key for the "Added" column.</summary>
    public DateTimeOffset AddedSortKey { get; }

    /// <summary>The numeric progress used as the sort key for the status column (0 when not measurable).</summary>
    public double ProgressSortKey => ProgressPercent;

    public bool IsDownloading => Status == DownloadStatus.Active;
    public bool IsPaused => Status == DownloadStatus.Paused;
    public bool IsQueued => Status == DownloadStatus.Queued;
    public bool IsCompleted => Status == DownloadStatus.Completed;
    public bool IsFailed => Status == DownloadStatus.Failed;
    public bool IsExpired => Status == DownloadStatus.Expired;

    /// <summary>Failed or expired — both render with the error (red) status colour.</summary>
    public bool IsError => IsFailed || IsExpired;

    /// <summary>Resume/start is offered for anything not already running or finished.</summary>
    public bool CanResume => Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Failed;

    /// <summary>Pause is offered only while actively transferring.</summary>
    public bool CanPause => Status == DownloadStatus.Active;

    /// <summary>Renew is offered when the link has expired (or failed and may need a fresh URL).</summary>
    public bool CanRenew => Status is DownloadStatus.Expired or DownloadStatus.Failed;

    /// <summary>The completed file can be opened from disk.</summary>
    public bool CanOpenFile => Status == DownloadStatus.Completed && FilePath is not null;

    /// <summary>Applies a fresh progress snapshot to the live columns (status label, bar, speed, ETA).</summary>
    public void ApplyProgress(DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Status = progress.Status;
        StatusLabel = BuildLabel(progress.Status, progress.Fraction);
        ShowProgressBar = HasBar(progress.Status) && progress.Fraction is not null;
        ProgressPercent = progress.Fraction is { } f ? Math.Clamp(f * 100, 0, 100) : 0;
        SpeedDisplay = progress.Status == DownloadStatus.Active ? ByteFormatter.FormatSpeed(progress.BytesPerSecond) : "—";
        EtaDisplay = progress.Status == DownloadStatus.Active ? TimeFormatter.FormatEta(progress.Eta) : "—";
    }

    /// <summary>
    /// Applies a bare status change (no progress payload) — keeps the last known percent so a pause shows
    /// "Paused · 74%" rather than dropping to 0, but clears the live speed/ETA which no longer apply.
    /// </summary>
    public void ApplyStatus(DownloadStatus status)
    {
        Status = status;
        double? fraction = ProgressPercent > 0 ? ProgressPercent / 100 : null;
        StatusLabel = BuildLabel(status, fraction);
        ShowProgressBar = HasBar(status) && fraction is not null;
        if (status != DownloadStatus.Active)
        {
            SpeedDisplay = "—";
            EtaDisplay = "—";
        }
    }

    /// <summary>
    /// Builds the status cell's label, pairing the state with its percentage where one applies
    /// (e.g. <c>Downloading · 33%</c>, <c>Paused · 74%</c>, <c>Expired — needs renew</c>). Pure.
    /// </summary>
    public static string BuildLabel(DownloadStatus status, double? fraction)
    {
        string? percent = fraction is { } f
            ? Math.Round(Math.Clamp(f, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
            : null;

        return status switch
        {
            DownloadStatus.Active => percent is null ? "Downloading" : $"Downloading · {percent}",
            DownloadStatus.Paused => percent is null ? "Paused" : $"Paused · {percent}",
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Completed => "Complete",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Expired => "Expired — needs renew",
            _ => status.ToString(),
        };
    }

    private static bool HasBar(DownloadStatus status) =>
        status is DownloadStatus.Active or DownloadStatus.Paused;

    private static string BuildSubLine(Download download)
    {
        string host = Uri.TryCreate(download.Url, UriKind.Absolute, out Uri? uri) ? uri.Host : download.Url;
        return string.IsNullOrWhiteSpace(host) ? download.Url : host;
    }

    private static string DeriveNameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string last = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : string.Empty;
            if (!string.IsNullOrWhiteSpace(last))
            {
                return Uri.UnescapeDataString(last);
            }
        }

        return url;
    }

    private static string? ResolveFilePath(Download download)
    {
        if (string.IsNullOrWhiteSpace(download.Directory) || string.IsNullOrWhiteSpace(download.Filename))
        {
            return null;
        }

        return Path.Combine(download.Directory!, download.Filename!);
    }
}
