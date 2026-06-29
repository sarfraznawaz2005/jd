namespace JustDownload.App.ViewModels;

/// <summary>
/// The fine-grained status the list's search bar can filter to, on top of the sidebar's
/// category/incomplete/completed view (TASK-134). <see cref="Any"/> applies no status constraint.
/// </summary>
public enum DownloadStatusFilter
{
    Any,
    Queued,
    Active,
    Paused,
    Completed,
    Failed,
    Expired,
}

/// <summary>How recently a download was added, for the list's date filter (TASK-134).</summary>
public enum DownloadDateFilter
{
    AnyTime,
    Today,
    Last7Days,
    Last30Days,
}
