namespace JustDownload.Core.Data.Migrations;

/// <summary>
/// Version 7 — adds the separate-streams media columns to <c>downloads</c> (TASK-154 increment ②):
/// <c>media_audio_url</c> (the audio stream URL for a SeparateStreams/DASH download; the video stream is the
/// row's <c>url</c>) and <c>media_container</c> (the preferred output container as the integer value of
/// <c>JustDownload.Core.Settings.MediaContainer</c>, <see langword="null"/> = default). Both are
/// <see langword="null"/> for plain and HLS downloads. Forward-only, applied once inside the runner's transaction.
/// </summary>
internal sealed class AddDownloadMediaStreamsMigration : IMigration
{
    public int Version => 7;

    public string Description => "Add downloads.media_audio_url / media_container for separate streams (TASK-154).";

    public string Sql =>
        """
        ALTER TABLE downloads ADD COLUMN media_audio_url TEXT;
        ALTER TABLE downloads ADD COLUMN media_container INTEGER;
        """;
}
