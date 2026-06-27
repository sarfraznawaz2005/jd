namespace JustDownload.Core.Transport;

/// <summary>
/// The parsed <c>Content-Range</c> a server returns with a <c>206 Partial Content</c> response
/// (TASK-023): the byte range actually served and, when known, the resource's total size. The engine
/// uses <see cref="TotalLength"/> to learn the full size from the first ranged response and
/// <see cref="From"/>/<see cref="To"/> to confirm the server honoured the requested window.
/// </summary>
/// <param name="From">The inclusive first byte served, or <see langword="null"/> if unspecified.</param>
/// <param name="To">The inclusive last byte served, or <see langword="null"/> if unspecified.</param>
/// <param name="TotalLength">The resource's total length, or <see langword="null"/> if the server sent "*".</param>
public readonly record struct ContentRange(long? From, long? To, long? TotalLength);
