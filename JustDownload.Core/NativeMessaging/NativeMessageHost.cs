using Microsoft.Extensions.Logging;

namespace JustDownload.Core.NativeMessaging;

/// <summary>
/// Drives the Native Messaging conversation (TASK-064, D8): it reads length-prefixed JSON messages from
/// an input stream (the browser's stdout → the host's stdin), dispatches each to the
/// <see cref="INativeMessageHandler"/>, and writes any reply back. It owns no socket — the only transport
/// is the supplied streams — satisfying "no open local port by default". The loop ends when the peer
/// closes the pipe or cancellation is requested.
/// </summary>
public sealed partial class NativeMessageHost
{
    private readonly INativeMessageHandler _handler;
    private readonly NativeHostOptions _options;
    private readonly ILogger<NativeMessageHost> _logger;

    public NativeMessageHost(
        INativeMessageHandler handler,
        NativeHostOptions options,
        ILogger<NativeMessageHost> logger)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _handler = handler;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Processes messages from <paramref name="input"/>, writing replies to <paramref name="output"/>,
    /// until the input is closed or cancelled.
    /// </summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? request = await NativeMessageCodec
                .ReadAsync(input, _options.MaxIncomingBytes, cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                LogClosed(_logger);
                return; // peer closed the pipe
            }

            string? response = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                await NativeMessageCodec.WriteAsync(output, response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Native messaging peer closed the connection.")]
    private static partial void LogClosed(ILogger logger);
}
