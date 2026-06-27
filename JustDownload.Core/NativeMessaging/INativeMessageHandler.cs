namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Handles one decoded native message and optionally produces a reply (TASK-064). Implementations parse
/// the request JSON, act on it (e.g. enqueue a download — wired in later tasks), and return the response
/// JSON, or <see langword="null"/> to send nothing back.
/// </summary>
public interface INativeMessageHandler
{
    /// <summary>Handles <paramref name="requestJson"/> and returns the reply JSON, or <see langword="null"/>.</summary>
    Task<string?> HandleAsync(string requestJson, CancellationToken cancellationToken = default);
}
