using System.Text;
using JustDownload.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace JustDownload.Core.Logging;

/// <summary>The shared <c>errors.log</c> path construction, so <see cref="ErrorLogFileProvider"/> (which
/// writes it) and <see cref="IErrorLogPathProvider"/> (which a host UI reads it from) never drift apart.</summary>
internal static class ErrorLogPath
{
    public static string Resolve(IAppInfoProvider appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        return Path.Combine(AppDataPaths.Directory(appInfo), "errors.log");
    }
}

/// <summary>
/// Appends Error/Critical-level log lines to a single <c>errors.log</c> file under the app-data directory
/// (TASK-179) — an internal diagnostic trail, not a user-facing feature: no Settings UI, no per-level
/// verbosity control. Before this, nothing was registered as an <see cref="ILoggerProvider"/> at all, so
/// even critical failures captured by <see cref="Diagnostics.IGlobalErrorHandler"/> were logged into a
/// pipeline with no destination. Registered alongside every other provider
/// (<see cref="ServiceCollectionExtensions.AddJustDownloadLogging"/>); redaction still applies upstream via
/// <see cref="RedactingLogger"/>. A write failure (disk full, permissions) is swallowed — logging must never
/// crash the app it's trying to help diagnose. The file is capped at 5 MB: once exceeded, it's rotated to
/// <c>errors.log.old</c> (overwriting any previous one) rather than growing unbounded.
/// </summary>
internal sealed class ErrorLogFileProvider : ILoggerProvider
{
    private const long MaxBytesBeforeRotation = 5 * 1024 * 1024;
    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>Creates a provider backed by an explicit file path (used by tests, avoiding the process-wide
    /// JUSTDOWNLOAD_DATA_DIR env var so parallel test runs can't interfere with each other).</summary>
    internal ErrorLogFileProvider(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _path = filePath;
    }

    public ErrorLogFileProvider(IAppInfoProvider appInfo) => _path = ErrorLogPath.Resolve(appInfo);

    public ILogger CreateLogger(string categoryName) => new ErrorLogFileLogger(categoryName, this);

    internal void WriteLine(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                RotateIfTooLarge();
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfTooLarge()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length < MaxBytesBeforeRotation)
        {
            return;
        }

        File.Move(_path, _path + ".old", overwrite: true);
    }

    public void Dispose()
    {
    }
}

/// <summary>The <see cref="ILogger"/> half of <see cref="ErrorLogFileProvider"/> — only Error/Critical ever
/// reaches <see cref="Log{TState}"/>.</summary>
internal sealed class ErrorLogFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly ErrorLogFileProvider _provider;

    public ErrorLogFileLogger(string categoryName, ErrorLogFileProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Error or LogLevel.Critical;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        string levelText = logLevel == LogLevel.Critical ? "CRIT" : "ERROR";
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{levelText}] {_categoryName}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        _provider.WriteLine(line);
    }
}
