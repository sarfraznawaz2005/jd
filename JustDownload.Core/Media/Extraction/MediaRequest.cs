namespace JustDownload.Core.Media.Extraction;

/// <summary>
/// The input to media extraction (TASK-036): the candidate <see cref="Url"/> plus the optional hints an
/// extractor uses to decide cheaply whether it can handle the URL — the <see cref="ContentType"/> observed
/// for it (e.g. captured by the browser extension) and any request <see cref="Headers"/> (cookies/referrer)
/// it must replay when fetching playlists or segments. Immutable so it can be shared across extractors.
/// </summary>
public sealed record MediaRequest
{
    /// <summary>The URL to inspect.</summary>
    public required Uri Url { get; init; }

    /// <summary>The observed <c>Content-Type</c> for the URL, if known (lets an extractor skip a probe).</summary>
    public string? ContentType { get; init; }

    /// <summary>Extra request headers (cookies/referrer) to replay when fetching the media.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}
