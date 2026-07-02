namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 8 — adds the <c>downloads.alternate_urls</c> column (TASK-144): a newline-separated list of
/// mirror URLs to fail over to, in order, once every retry against the current <c>url</c> is exhausted. A
/// URL cannot itself contain a raw newline, so this is the lightest possible encoding for the list — no JSON
/// dependency needed. <see langword="null"/> (the default for existing rows) means "no configured mirrors".
/// Forward-only, applied once inside the runner's transaction.
/// </summary>
internal sealed class AddDownloadAlternateUrlsMigration : IMigration
{
    public int Version => 8;

    public string Description => "Add downloads.alternate_urls for mirror failover (TASK-144).";

    public string Sql => "ALTER TABLE downloads ADD COLUMN alternate_urls TEXT;";
}
