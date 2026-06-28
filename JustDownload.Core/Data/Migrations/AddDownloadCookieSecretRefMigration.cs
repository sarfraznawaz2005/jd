namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 3 — adds the <c>cookie_secret_ref</c> column to <c>downloads</c> (TASK-091, §5). It holds only the
/// opaque keychain reference for cookies captured by the browser extension (never the cookies themselves), so
/// authenticated/signed downloads can resend the <c>Cookie</c> header on download/resume. Existing rows
/// default to <c>NULL</c> (no captured cookies). Forward-only, applied once inside the runner's transaction.
/// </summary>
internal sealed class AddDownloadCookieSecretRefMigration : IMigration
{
    public int Version => 3;

    public string Description => "Add downloads.cookie_secret_ref for keychain-backed request cookies (TASK-091).";

    public string Sql => "ALTER TABLE downloads ADD COLUMN cookie_secret_ref TEXT;";
}
