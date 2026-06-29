using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Logging;

/// <summary>
/// A live, mutable minimum-log-level authority (TASK-127). Because every logger is wrapped by
/// <see cref="RedactingLogger"/>, gating on this switch in <see cref="RedactingLogger.IsEnabled"/> lets the
/// app's verbosity be changed at runtime (from settings) and take effect immediately, without rebuilding the
/// logger factory. Seeded from <see cref="LoggingOptions.MinimumLevel"/>.
/// </summary>
public interface ILogLevelSwitch
{
    /// <summary>The minimum level currently emitted; messages below it are suppressed everywhere.</summary>
    LogLevel Minimum { get; set; }
}

internal sealed class LogLevelSwitch : ILogLevelSwitch
{
    public LogLevelSwitch(LogLevel initial) => Minimum = initial;

    // LogLevel is an int-backed enum; reads/writes are atomic, which is all this cross-thread flag needs.
    public LogLevel Minimum { get; set; }
}
