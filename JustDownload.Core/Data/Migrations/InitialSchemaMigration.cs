namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 1 — the initial schema covering every table in PRD §4.4: <c>downloads</c>,
/// <c>segments</c>, <c>auth</c>, <c>proxies</c>, <c>extractor_jobs</c>, <c>settings</c>, and
/// <c>site_blacklist</c>.
/// <para>
/// Credentials are <b>never</b> stored here: <c>auth</c> and <c>proxies</c> keep only a
/// <c>secret_ref</c> pointer into the OS keychain (CLAUDE.md §5 "secrets at rest"). Columns that
/// collide with SQLite keywords (<c>index</c>, <c>start</c>, <c>end</c>, <c>key</c>) are quoted.
/// Foreign keys cascade so deleting a download cleans up its segments and auth row, matching the
/// "pause/cancel leaves no orphans" guardrail.
/// </para>
/// </summary>
internal sealed class InitialSchemaMigration : IMigration
{
    public int Version => 1;

    public string Description =>
        "Initial schema: downloads, segments, auth, proxies, extractor_jobs, settings, site_blacklist.";

    public string Sql => """
        CREATE TABLE downloads (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            url             TEXT    NOT NULL,
            referrer        TEXT    NULL,
            filename        TEXT    NULL,
            dir             TEXT    NULL,
            total_bytes     INTEGER NULL,
            status          TEXT    NOT NULL,
            category_type   TEXT    NULL,
            category_status TEXT    NULL,
            etag            TEXT    NULL,
            last_modified   TEXT    NULL,
            created_at      TEXT    NOT NULL,
            completed_at    TEXT    NULL,
            error           TEXT    NULL,
            max_connections INTEGER NULL,
            speed_limit     INTEGER NULL
        );

        CREATE TABLE segments (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            download_id INTEGER NOT NULL,
            "index"     INTEGER NOT NULL,
            "start"     INTEGER NOT NULL,
            "end"       INTEGER NOT NULL,
            downloaded  INTEGER NOT NULL DEFAULT 0,
            state       TEXT    NOT NULL,
            FOREIGN KEY (download_id) REFERENCES downloads (id) ON DELETE CASCADE
        );

        CREATE INDEX ix_segments_download_id ON segments (download_id);

        CREATE TABLE auth (
            download_id INTEGER NOT NULL,
            scheme      TEXT    NOT NULL,
            realm       TEXT    NULL,
            username    TEXT    NULL,
            secret_ref  TEXT    NULL,
            PRIMARY KEY (download_id, scheme),
            FOREIGN KEY (download_id) REFERENCES downloads (id) ON DELETE CASCADE
        );

        CREATE TABLE proxies (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            type       TEXT    NOT NULL,
            host       TEXT    NOT NULL,
            port       INTEGER NOT NULL,
            username   TEXT    NULL,
            secret_ref TEXT    NULL
        );

        CREATE TABLE extractor_jobs (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            page_url     TEXT    NOT NULL,
            playlist_url TEXT    NULL,
            variant      TEXT    NULL,
            key_uri      TEXT    NULL,
            status       TEXT    NOT NULL
        );

        CREATE TABLE settings (
            "key" TEXT PRIMARY KEY,
            value TEXT NULL
        );

        CREATE TABLE site_blacklist (
            domain TEXT NOT NULL,
            scope  TEXT NOT NULL,
            PRIMARY KEY (domain, scope)
        );
        """;
}
