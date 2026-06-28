namespace JustDownload.Core.Media.Streams;

/// <summary>The role a media stream plays when video and audio are delivered separately (TASK-039).</summary>
public enum StreamRole
{
    /// <summary>The video-only elementary stream.</summary>
    Video = 0,

    /// <summary>The audio-only elementary stream.</summary>
    Audio = 1,
}

/// <summary>
/// One downloadable elementary stream of a separate-streams media item (TASK-039): its source
/// <see cref="Url"/>, its <see cref="Role"/>, the <see cref="DestinationPath"/> to write it to, the desired
/// <see cref="Connections"/>, and any request <see cref="Headers"/> to replay. The two specs of a
/// video+audio pair are downloaded concurrently and later muxed (TASK-041).
/// </summary>
public sealed record MediaStreamSpec
{
    /// <summary>The stream's source URL.</summary>
    public required Uri Url { get; init; }

    /// <summary>Whether this is the video or audio stream.</summary>
    public required StreamRole Role { get; init; }

    /// <summary>The absolute path to write this stream's file to.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>The requested connection count for this stream, or <see langword="null"/> for the default.</summary>
    public int? Connections { get; init; }

    /// <summary>Extra request headers (cookies/referrer) for this stream.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}
