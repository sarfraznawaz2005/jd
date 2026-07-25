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
    [NotifyPropertyChangedFor(nameof(CanRestart))]
    [NotifyPropertyChangedFor(nameof(CanOpenFile))]
    private DownloadStatus _status;

    [ObservableProperty]
    private string _statusLabel = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _showProgressBar;

    /// <summary>
    /// Whether the bar should run as a marquee instead of filling to <see cref="ProgressPercent"/>. Set for a
    /// source that never advertises its size (a media stream, a range-less server): there is no percentage to
    /// fill, and a determinate bar frozen at 0% reads as "stuck" rather than "size unknown" (user-reported).
    /// </summary>
    [ObservableProperty]
    private bool _isProgressIndeterminate;

    [ObservableProperty]
    private string _speedDisplay = "—";

    [ObservableProperty]
    private string _etaDisplay = "—";

    /// <summary>The formatted size column (e.g. <c>52.5 MB</c>) or <c>—</c> while the size is unknown.</summary>
    [ObservableProperty]
    private string _sizeDisplay = "—";

    /// <summary>Bytes fetched so far, so a bare status change can keep showing the unknown-size measure.</summary>
    private long _downloadedBytes;

    /// <summary>The last reported phase, so a bare status change doesn't silently reset it to Transferring.</summary>
    private DownloadPhase _phase;

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
        SizeDisplay = FormatSize(download.TotalBytes);
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

    /// <summary>
    /// Total size in bytes when known, used as the sort key for the size column. Unknown-size sources only
    /// learn their total once the transfer ends, so this tracks the engine's snapshots rather than staying at
    /// the (null) value the record was created with.
    /// </summary>
    public long? TotalBytes { get; private set; }


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

    /// <summary>
    /// Re-download (fetch again from byte zero) is offered for anything not currently transferring — an
    /// active download must be paused first so its workers release the destination file.
    /// </summary>
    public bool CanRestart => Status != DownloadStatus.Active;

    /// <summary>The completed file can be opened from disk.</summary>
    public bool CanOpenFile => Status == DownloadStatus.Completed && FilePath is not null;

    /// <summary>Applies a fresh progress snapshot to the live columns (status label, bar, speed, ETA).</summary>
    public void ApplyProgress(DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Status = progress.Status;
        _downloadedBytes = progress.DownloadedBytes;
        _phase = progress.Phase;
        StatusLabel = BuildLabel(progress.Status, progress.Fraction, progress.DownloadedBytes, progress.Phase);
        IsProgressIndeterminate = IsIndeterminate(progress.Status, progress.Fraction, progress.Phase);
        ShowProgressBar = HasBar(progress.Status) && (progress.Fraction is not null || IsProgressIndeterminate);
        ProgressPercent = progress.Fraction is { } f ? Math.Clamp(f * 100, 0, 100) : 0;
        SpeedDisplay = progress.Status == DownloadStatus.Active ? ByteFormatter.FormatSpeed(progress.BytesPerSecond) : "—";
        EtaDisplay = progress.Status == DownloadStatus.Active ? TimeFormatter.FormatEta(progress.Eta) : "—";

        // An unknown-size source only reveals its total when the transfer ends; adopt it so the size column
        // stops reading "—" for a file that is now sitting complete on disk.
        if (progress.TotalBytes is > 0)
        {
            TotalBytes = progress.TotalBytes;
            SizeDisplay = FormatSize(progress.TotalBytes);
        }
    }

    /// <summary>
    /// Applies a bare status change (no progress payload) — keeps the last known percent so a pause shows
    /// "Paused · 74%" rather than dropping to 0, but clears the live speed/ETA which no longer apply.
    /// </summary>
    public void ApplyStatus(DownloadStatus status)
    {
        Status = status;
        double? fraction = ProgressPercent > 0 ? ProgressPercent / 100 : null;
        StatusLabel = BuildLabel(status, fraction, _downloadedBytes, _phase);
        IsProgressIndeterminate = IsIndeterminate(status, fraction, _phase);
        ShowProgressBar = HasBar(status) && (fraction is not null || IsProgressIndeterminate);
        if (status != DownloadStatus.Active)
        {
            SpeedDisplay = "—";
            EtaDisplay = "—";
        }
    }

    /// <summary>
    /// Builds the status cell's label, pairing the state with the best measure of how far it has got:
    /// a percentage when the total size is known (<c>Downloading · 33%</c>, <c>Paused · 74%</c>), otherwise
    /// the bytes fetched so far (<c>Downloading · 106.9 MB</c>) so an unknown-size download still shows it is
    /// making headway. A download past its transfer names the work it is actually doing instead. Pure.
    /// </summary>
    public static string BuildLabel(
        DownloadStatus status,
        double? fraction,
        long downloadedBytes = 0,
        DownloadPhase phase = DownloadPhase.Transferring)
    {
        if (status == DownloadStatus.Active && phase == DownloadPhase.Processing)
        {
            // Joining segments and muxing streams moves no bytes, so any measure here would sit frozen and
            // read as a stalled download (user-reported).
            return "Merging streams…";
        }

        string? measure = fraction is { } f
            ? Math.Round(Math.Clamp(f, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
            : downloadedBytes > 0 ? ByteFormatter.FormatSize(downloadedBytes) : null;

        return status switch
        {
            DownloadStatus.Active => measure is null ? "Downloading" : $"Downloading · {measure}",
            DownloadStatus.Paused => measure is null ? "Paused" : $"Paused · {measure}",
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Completed => "Complete",
            DownloadStatus.Failed => "Failed",
            DownloadStatus.Expired => "Expired — needs renew",
            _ => status.ToString(),
        };
    }

    private static bool HasBar(DownloadStatus status) =>
        status is DownloadStatus.Active or DownloadStatus.Paused;

    /// <summary>
    /// A marquee is only honest while work is actually happening: a paused unknown-size download keeps no bar
    /// at all rather than animating as though it were still transferring. Post-processing is unmeasurable
    /// work in progress, so it runs the marquee even for a source whose transfer had a percentage.
    /// </summary>
    private static bool IsIndeterminate(DownloadStatus status, double? fraction, DownloadPhase phase) =>
        status == DownloadStatus.Active && (fraction is null || phase == DownloadPhase.Processing);

    private static string FormatSize(long? totalBytes) =>
        totalBytes is > 0 ? ByteFormatter.FormatSize(totalBytes.Value) : "—";

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
