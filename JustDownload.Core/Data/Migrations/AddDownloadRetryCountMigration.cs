namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 4 — adds the <c>retry_count</c> column to <c>downloads</c> (TASK-131). It records how many times
/// the engine auto-retried a transient failure for the download, so the count survives restarts and is
/// visible to the UI. Existing rows default to <c>0</c>. Forward-only, applied once inside the runner's
/// transaction.
/// </summary>
internal sealed class AddDownloadRetryCountMigration : IMigration
{
    public int Version => 4;

    public string Description => "Add downloads.retry_count for auto-retry tracking (TASK-131).";

    public string Sql => "ALTER TABLE downloads ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0;";
}
