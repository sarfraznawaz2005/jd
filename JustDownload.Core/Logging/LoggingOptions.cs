using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Logging;

/// <summary>
/// Configuration for the engine's logging pipeline. Surfaced through
/// <see cref="ServiceCollectionExtensions.AddJustDownloadCore"/> so a host (App, NativeHost, a
/// future CLI, or a test) can set the verbosity without touching Core internals — satisfying
/// TASK-016's "log level configurable" requirement.
/// </summary>
public sealed class LoggingOptions
{
    /// <summary>
    /// The minimum <see cref="LogLevel"/> that will be emitted. Defaults to
    /// <see cref="LogLevel.Information"/> — quiet enough for the light-&-fast promise, verbose enough
    /// to be useful. Set to <see cref="LogLevel.Debug"/> for diagnostics or
    /// <see cref="LogLevel.Warning"/> to go near-silent.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
