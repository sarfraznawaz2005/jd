namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 5 — adds the per-download proxy override columns to <c>downloads</c> (TASK-153). A download may
/// route through a different proxy than the global one; these columns persist that override. <c>proxy_kind</c>
/// is <see langword="null"/> for the common case of "no override" (use the global proxy). The password is
/// never stored here — only the opaque OS-keychain reference (<c>proxy_password_secret_ref</c>, §5). Existing
/// rows get <see langword="null"/> for every new column. Forward-only, applied once inside the runner's transaction.
/// </summary>
internal sealed class AddDownloadProxyOverrideMigration : IMigration
{
    public int Version => 5;

    public string Description => "Add downloads.proxy_* override columns for per-download proxy (TASK-153).";

    public string Sql =>
        """
        ALTER TABLE downloads ADD COLUMN proxy_kind INTEGER;
        ALTER TABLE downloads ADD COLUMN proxy_host TEXT;
        ALTER TABLE downloads ADD COLUMN proxy_port INTEGER;
        ALTER TABLE downloads ADD COLUMN proxy_username TEXT;
        ALTER TABLE downloads ADD COLUMN proxy_domain TEXT;
        ALTER TABLE downloads ADD COLUMN proxy_password_secret_ref TEXT;
        """;
}
