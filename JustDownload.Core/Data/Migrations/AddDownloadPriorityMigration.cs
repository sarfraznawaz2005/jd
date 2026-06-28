namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 2 — adds the <c>priority</c> column to <c>downloads</c> (TASK-072, US-16). Existing rows default
/// to <c>0</c>, preserving their current (creation-order) queue position. Forward-only, applied once by the
/// runner inside its transaction.
/// </summary>
internal sealed class AddDownloadPriorityMigration : IMigration
{
    public int Version => 2;

    public string Description => "Add downloads.priority for queue ordering (TASK-072).";

    public string Sql => "ALTER TABLE downloads ADD COLUMN priority INTEGER NOT NULL DEFAULT 0;";
}
