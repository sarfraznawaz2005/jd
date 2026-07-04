using JustDownload.Core.Abstractions;

namespace JustDownload.Core.Logging;

/// <summary>
/// Resolves the on-disk path of the Error/Critical log file written by <see cref="ErrorLogFileProvider"/>, so
/// a host UI can offer a "view error logs" action without duplicating (or drifting from) that path
/// construction (TASK-179 follow-up).
/// </summary>
public interface IErrorLogPathProvider
{
    /// <summary>The full path to <c>errors.log</c>. May not exist yet if nothing has been logged.</summary>
    string FilePath { get; }
}

internal sealed class ErrorLogPathProvider : IErrorLogPathProvider
{
    public ErrorLogPathProvider(IAppInfoProvider appInfo) => FilePath = ErrorLogPath.Resolve(appInfo);

    public string FilePath { get; }
}
