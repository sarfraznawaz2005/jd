namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 6 — adds the <c>media_kind</c> column to <c>downloads</c> (TASK-154). It records the media
/// download path to take, as the integer value of <c>JustDownload.Core.Media.Extraction.MediaKind</c>:
/// <see langword="null"/> or <c>0</c> (Progressive) means a plain segmented-HTTP download; <c>1</c> (Hls)
/// routes the start through the media coordinator (segments &#8594; concat) instead. Existing rows get
/// <see langword="null"/>. Forward-only, applied once inside the runner's transaction.
/// </summary>
internal sealed class AddDownloadMediaKindMigration : IMigration
{
    public int Version => 6;

    public string Description => "Add downloads.media_kind for media-variant downloads (TASK-154).";

    public string Sql => "ALTER TABLE downloads ADD COLUMN media_kind INTEGER;";
}
