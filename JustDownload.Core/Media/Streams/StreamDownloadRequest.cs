using JustDownload.Core.Downloading;

namespace JustDownload.Core.Media.Streams;

/// <summary>
/// A request to download one stream of a separate-streams pair (TASK-039): the <see cref="Spec"/> plus this
/// stream's own resume checkpoint and progress sinks, so each stream reports progress and a per-connection
/// segment bar independently of the other (AC1) and resumes from its own committed ranges (AC2).
/// </summary>
public sealed record StreamDownloadRequest
{
    /// <summary>What to download and where.</summary>
    public required MediaStreamSpec Spec { get; init; }

    /// <summary>This stream's resume checkpoint (seeds resume and records committed writes), or <see langword="null"/>.</summary>
    public ReceivedRanges? Received { get; init; }

    /// <summary>This stream's cumulative bytes-written sink, or <see langword="null"/>.</summary>
    public IProgress<long>? Progress { get; init; }

    /// <summary>This stream's per-connection (segment bar) progress sink, or <see langword="null"/>.</summary>
    public IProgress<ConnectionProgress>? ConnectionProgress { get; init; }
}
