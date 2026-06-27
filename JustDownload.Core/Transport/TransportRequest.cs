namespace JustDownload.Core.Transport;

/// <summary>The HTTP method a <see cref="TransportRequest"/> uses (TASK-023).</summary>
public enum TransportMethod
{
    /// <summary>Fetch the resource (optionally a byte range of it).</summary>
    Get = 0,

    /// <summary>Fetch only headers/metadata — used by the range-capability probe (TASK-024).</summary>
    Head = 1,
}

/// <summary>
/// A protocol-agnostic transport request (TASK-023): a target <see cref="Uri"/>, a method, an optional
/// byte <see cref="Range"/>, and any extra request <see cref="Headers"/> (e.g. cookies/referrer captured
/// by the browser extension, or a conditional <c>If-Range</c>). Immutable so a request can be safely
/// reused across the connections of a segmented download.
/// </summary>
public sealed record TransportRequest
{
    /// <summary>The resource to fetch.</summary>
    public required Uri Uri { get; init; }

    /// <summary>The request method. Defaults to <see cref="TransportMethod.Get"/>.</summary>
    public TransportMethod Method { get; init; } = TransportMethod.Get;

    /// <summary>The byte range to request, or <see langword="null"/> for the whole resource.</summary>
    public ByteRange? Range { get; init; }

    /// <summary>Extra request headers to send (added without overriding transport-managed headers).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}
