using JustDownload.Core.Downloading;

namespace JustDownload.Core.Media.Streams;

/// <summary>
/// The outcome of one stream within a separate-streams download (TASK-039). When <see cref="Succeeded"/> is
/// <see langword="false"/> the <see cref="Error"/> explains why and the partially-written file plus its
/// resume checkpoint are left intact so the stream can be retried/resumed (AC2).
/// </summary>
public sealed record StreamOutcome
{
    /// <summary>Which stream this outcome is for.</summary>
    public required StreamRole Role { get; init; }

    /// <summary>The file this stream was written to.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Whether this stream completed successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The completed download result, present when <see cref="Succeeded"/> is <see langword="true"/>.</summary>
    public DownloadResult? Result { get; init; }

    /// <summary>The failure cause, present when <see cref="Succeeded"/> is <see langword="false"/>.</summary>
    public Exception? Error { get; init; }
}

/// <summary>
/// The combined outcome of a video+audio separate-streams download (TASK-039): the per-stream
/// <see cref="Video"/> and <see cref="Audio"/> outcomes. <see cref="AllSucceeded"/> is the gate for muxing
/// (TASK-041) — a partial result means one stream can still be resumed without re-fetching the other.
/// </summary>
/// <param name="Video">The video stream's outcome.</param>
/// <param name="Audio">The audio stream's outcome.</param>
public sealed record SeparateStreamResult(StreamOutcome Video, StreamOutcome Audio)
{
    /// <summary>Whether both streams completed successfully (ready to mux).</summary>
    public bool AllSucceeded => Video.Succeeded && Audio.Succeeded;
}
